using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;

namespace DataWarehouse.Plugins.WinFspDriver;

/// <summary>
/// Implements file operation handlers for the WinFSP filesystem.
/// Provides CreateFile, ReadFile, WriteFile, DeleteFile, MoveFile, FindFiles, and FileInfo operations.
/// </summary>
public sealed class WinFspOperations : IDisposable
{
    private readonly WinFspFileSystem _fileSystem;
    private readonly WinFspCacheManager _cacheManager;
    private readonly WinFspSecurityHandler _securityHandler;
    private readonly WinFspConfig _config;
    private readonly ConcurrentDictionary<IntPtr, OpenFileContext> _openFiles;
    private readonly SemaphoreSlim _operationSemaphore;
    private readonly ReaderWriterLockSlim _renameLock;
    private long _nextFileHandle;
    private bool _disposed;

    /// <summary>
    /// Initializes file operations handler.
    /// </summary>
    public WinFspOperations(
        WinFspFileSystem fileSystem,
        WinFspCacheManager cacheManager,
        WinFspSecurityHandler securityHandler,
        WinFspConfig config)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _securityHandler = securityHandler ?? throw new ArgumentNullException(nameof(securityHandler));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _openFiles = new ConcurrentDictionary<IntPtr, OpenFileContext>();
        _operationSemaphore = new SemaphoreSlim(_config.MaxConcurrentOperations, _config.MaxConcurrentOperations);
        _renameLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
    }

    #region File Creation and Opening

    /// <summary>
    /// Creates a new file or directory.
    /// </summary>
    /// <param name="fileName">Full path of the file/directory to create.</param>
    /// <param name="createOptions">Creation options from WinFSP.</param>
    /// <param name="grantedAccess">Access rights granted to the caller.</param>
    /// <param name="fileAttributes">Initial file attributes.</param>
    /// <param name="securityDescriptor">Security descriptor for the new file.</param>
    /// <param name="allocationSize">Initial allocation size for files.</param>
    /// <param name="fileContext">Output file context handle.</param>
    /// <param name="fileInfo">Output file information.</param>
    /// <returns>NTSTATUS code.</returns>
    public int Create(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        uint fileAttributes,
        IntPtr securityDescriptor,
        ulong allocationSize,
        out IntPtr fileContext,
        out WinFspNative.FileInfo fileInfo)
    {
        fileContext = IntPtr.Zero;
        fileInfo = default;

        try
        {
            if (!_operationSemaphore.Wait(_config.OperationTimeoutMs))
                return WinFspNative.Status.Timeout;

            try
            {
                // Validate path
                if (string.IsNullOrEmpty(fileName) || !ValidatePath(fileName))
                    return WinFspNative.Status.ObjectNameInvalid;

                // Check access
                if (!CheckAccessForCreate(fileName, grantedAccess))
                    return WinFspNative.Status.AccessDenied;

                var isDirectory = (createOptions & (uint)WinFspNative.CreateOptions.DirectoryFile) != 0;
                var normalizedPath = NormalizePath(fileName);

                // Check if file already exists
                if (_fileSystem.PathExists(normalizedPath))
                    return WinFspNative.Status.ObjectNameCollision;

                // Check parent exists
                var parentPath = GetParentPath(normalizedPath);
                if (!string.IsNullOrEmpty(parentPath) && !_fileSystem.PathExists(parentPath))
                    return WinFspNative.Status.ObjectPathNotFound;

                // Create the file/directory
                var entry = new FileEntry
                {
                    Path = normalizedPath,
                    IsDirectory = isDirectory,
                    Attributes = (WinFspNative.FileAttributes)fileAttributes,
                    AllocationSize = allocationSize,
                    FileSize = 0,
                    CreationTime = WinFspNative.GetCurrentFileTime(),
                    LastAccessTime = WinFspNative.GetCurrentFileTime(),
                    LastWriteTime = WinFspNative.GetCurrentFileTime(),
                    ChangeTime = WinFspNative.GetCurrentFileTime(),
                    IndexNumber = GenerateIndexNumber()
                };

                if (isDirectory)
                    entry.Attributes |= WinFspNative.FileAttributes.Directory;

                // Set security descriptor
                if (securityDescriptor != IntPtr.Zero)
                {
                    var sd = CopySecurityDescriptor(securityDescriptor);
                    _securityHandler.SetSecurityDescriptor(normalizedPath, sd);
                }
                else
                {
                    var inheritedSd = _securityHandler.CreateInheritedSecurityDescriptor(parentPath, isDirectory);
                    _securityHandler.SetSecurityDescriptor(normalizedPath, inheritedSd);
                }

                // Add to filesystem
                _fileSystem.AddEntry(entry);

                // Create open file context
                var context = new OpenFileContext
                {
                    Path = normalizedPath,
                    IsDirectory = isDirectory,
                    GrantedAccess = grantedAccess,
                    DeleteOnClose = (createOptions & (uint)WinFspNative.CreateOptions.DeleteOnClose) != 0
                };

                var handle = AllocateFileHandle();
                _openFiles[handle] = context;
                fileContext = handle;

                // Fill file info
                fileInfo = CreateFileInfo(entry);

                return WinFspNative.Status.Success;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }
        catch (Exception)
        {
            return WinFspNative.Status.InternalError;
        }
    }

    /// <summary>
    /// Opens an existing file or directory.
    /// </summary>
    public int Open(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        out IntPtr fileContext,
        out WinFspNative.FileInfo fileInfo)
    {
        fileContext = IntPtr.Zero;
        fileInfo = default;

        try
        {
            if (!_operationSemaphore.Wait(_config.OperationTimeoutMs))
                return WinFspNative.Status.Timeout;

            try
            {
                var normalizedPath = NormalizePath(fileName);

                // Check if file exists
                var entry = _fileSystem.GetEntry(normalizedPath);
                if (entry == null)
                    return WinFspNative.Status.ObjectNameNotFound;

                // Check access
                if (!CheckAccess(normalizedPath, grantedAccess, entry.IsDirectory))
                    return WinFspNative.Status.AccessDenied;

                // Validate directory flag
                var wantDirectory = (createOptions & (uint)WinFspNative.CreateOptions.DirectoryFile) != 0;
                var wantFile = (createOptions & (uint)WinFspNative.CreateOptions.NonDirectoryFile) != 0;

                if (wantDirectory && !entry.IsDirectory)
                    return WinFspNative.Status.NotADirectory;
                if (wantFile && entry.IsDirectory)
                    return WinFspNative.Status.FileIsADirectory;

                // Create open file context
                var context = new OpenFileContext
                {
                    Path = normalizedPath,
                    IsDirectory = entry.IsDirectory,
                    GrantedAccess = grantedAccess,
                    DeleteOnClose = (createOptions & (uint)WinFspNative.CreateOptions.DeleteOnClose) != 0
                };

                var handle = AllocateFileHandle();
                _openFiles[handle] = context;
                fileContext = handle;

                // Update access time
                entry.LastAccessTime = WinFspNative.GetCurrentFileTime();
                fileInfo = CreateFileInfo(entry);

                return WinFspNative.Status.Success;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }
        catch (Exception)
        {
            return WinFspNative.Status.InternalError;
        }
    }

    /// <summary>
    /// Closes a file handle.
    /// </summary>
    public void Close(IntPtr fileContext)
    {
        if (fileContext == IntPtr.Zero)
            return;

        if (_openFiles.TryRemove(fileContext, out var context))
        {
            // Flush any pending writes
            var chunks = _cacheManager.FlushWriteBuffer(context.Path);
            if (chunks.Count > 0)
            {
                PerformPendingWrites(context.Path, chunks);
            }

            // Delete if marked for delete-on-close
            if (context.DeleteOnClose)
            {
                _fileSystem.DeleteEntry(context.Path);
                _securityHandler.RemoveSecurityDescriptor(context.Path);
                _cacheManager.InvalidateReadCache(context.Path);
            }
        }
    }

    /// <summary>
    /// Cleanup handler called before close.
    /// </summary>
    public void Cleanup(IntPtr fileContext, string fileName, uint flags)
    {
        if (fileContext == IntPtr.Zero)
            return;

        // Flush write buffers on cleanup
        if (_openFiles.TryGetValue(fileContext, out var context))
        {
            var chunks = _cacheManager.FlushWriteBuffer(context.Path);
            if (chunks.Count > 0)
            {
                PerformPendingWrites(context.Path, chunks);
            }
        }
    }

    #endregion

    #region Read/Write Operations

    /// <summary>
    /// Reads data from a file.
    /// </summary>
    public int Read(
        IntPtr fileContext,
        IntPtr buffer,
        ulong offset,
        uint length,
        out uint bytesTransferred)
    {
        bytesTransferred = 0;

        try
        {
            if (!_openFiles.TryGetValue(fileContext, out var context))
                return WinFspNative.Status.FileClosed;

            if (context.IsDirectory)
                return WinFspNative.Status.FileIsADirectory;

            var entry = _fileSystem.GetEntry(context.Path);
            if (entry == null)
                return WinFspNative.Status.ObjectNameNotFound;

            // Check EOF
            if ((long)offset >= (long)entry.FileSize)
                return WinFspNative.Status.EndOfFile;

            // Calculate actual read length
            var actualLength = (uint)Math.Min(length, (ulong)entry.FileSize - offset);

            // Try cache first
            if (_cacheManager.TryGetReadCache(context.Path, (long)offset, (int)actualLength, out var cachedData) && cachedData != null)
            {
                Marshal.Copy(cachedData, 0, buffer, cachedData.Length);
                bytesTransferred = (uint)cachedData.Length;
                return WinFspNative.Status.Success;
            }

            // Read from backend with prefetch
            var prefetchSize = _cacheManager.GetPrefetchSize(context.Path, (long)offset);
            var readLength = Math.Min(actualLength + (uint)prefetchSize, (uint)((ulong)entry.FileSize - offset));

            var data = _fileSystem.ReadData(context.Path, (long)offset, (int)readLength);
            if (data == null)
                return WinFspNative.Status.InternalError;

            // Add to cache
            _cacheManager.AddToReadCache(context.Path, (long)offset, data);

            // Copy to buffer
            var transferAmount = Math.Min(data.Length, (int)actualLength);
            Marshal.Copy(data, 0, buffer, transferAmount);
            bytesTransferred = (uint)transferAmount;

            // Update access time
            entry.LastAccessTime = WinFspNative.GetCurrentFileTime();

            return WinFspNative.Status.Success;
        }
        catch (Exception)
        {
            return WinFspNative.Status.InternalError;
        }
    }

    /// <summary>
    /// Writes data to a file.
    /// </summary>
    public int Write(
        IntPtr fileContext,
        IntPtr buffer,
        ulong offset,
        uint length,
        bool writeToEndOfFile,
        bool constrainedIo,
        out uint bytesTransferred,
        out WinFspNative.FileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;

        try
        {
            if (!_openFiles.TryGetValue(fileContext, out var context))
                return WinFspNative.Status.FileClosed;

            if (context.IsDirectory)
                return WinFspNative.Status.FileIsADirectory;

            var entry = _fileSystem.GetEntry(context.Path);
            if (entry == null)
                return WinFspNative.Status.ObjectNameNotFound;

            // Calculate actual offset
            var actualOffset = writeToEndOfFile ? (ulong)entry.FileSize : offset;

            // For constrained I/O, don't extend file
            if (constrainedIo)
            {
                if ((long)actualOffset >= (long)entry.FileSize)
                    return WinFspNative.Status.Success;

                length = (uint)Math.Min(length, (ulong)entry.FileSize - actualOffset);
            }

            // Copy data from buffer
            var data = new byte[length];
            Marshal.Copy(buffer, data, 0, (int)length);

            // Invalidate read cache for this region
            _cacheManager.InvalidateReadCache(context.Path);

            // Try buffered write
            if (_cacheManager.TryBufferWrite(context.Path, (long)actualOffset, data))
            {
                bytesTransferred = length;
            }
            else
            {
                // Flush existing buffer and write immediately
                var pendingChunks = _cacheManager.FlushWriteBuffer(context.Path);
                if (pendingChunks.Count > 0)
                {
                    PerformPendingWrites(context.Path, pendingChunks);
                }

                // Write current data
                _fileSystem.WriteData(context.Path, (long)actualOffset, data);
                bytesTransferred = length;
            }

            // Update file size if needed
            var newEndOffset = actualOffset + length;
            if ((long)newEndOffset > (long)entry.FileSize)
            {
                entry.FileSize = (long)newEndOffset;
            }

            // Update timestamps
            var now = WinFspNative.GetCurrentFileTime();
            entry.LastWriteTime = now;
            entry.ChangeTime = now;

            fileInfo = CreateFileInfo(entry);

            return WinFspNative.Status.Success;
        }
        catch (Exception)
        {
            return WinFspNative.Status.InternalError;
        }
    }

    /// <summary>
    /// Flushes file buffers.
    /// </summary>
    public int Flush(IntPtr fileContext, out WinFspNative.FileInfo fileInfo)
    {
        fileInfo = default;

        try
        {
            if (fileContext == IntPtr.Zero)
            {
                // Flush all files
                _cacheManager.FlushAllWriteBuffersAsync().Wait(_config.OperationTimeoutMs);
                return WinFspNative.Status.Success;
            }

            if (!_openFiles.TryGetValue(fileContext, out var context))
                return WinFspNative.Status.FileClosed;

            var chunks = _cacheManager.FlushWriteBuffer(context.Path);
            if (chunks.Count > 0)
            {
                PerformPendingWrites(context.Path, chunks);
            }

            var entry = _fileSystem.GetEntry(context.Path);
            if (entry != null)
            {
                fileInfo = CreateFileInfo(entry);
            }

            return WinFspNative.Status.Success;
        }
        catch (Exception)
        {
            return WinFspNative.Status.InternalError;
        }
    }

    #endregion

    #region Delete and Rename Operations

    /// <summary>
    /// Checks if a file can be deleted.
    /// </summary>
    public int CanDelete(IntPtr fileContext, string fileName)
    {
        try
        {
            var normalizedPath = NormalizePath(fileName);
            var entry = _fileSystem.GetEntry(normalizedPath);

            if (entry == null)
                return WinFspNative.Status.ObjectNameNotFound;

            // Check if directory is empty
            if (entry.IsDirectory && _fileSystem.HasChildren(normalizedPath))
                return WinFspNative.Status.DirectoryNotEmpty;

            // Check delete permission
            if (!CheckAccess(normalizedPath, (uint)WinFspNative.AccessRights.Delete, entry.IsDirectory))
                return WinFspNative.Status.AccessDenied;

            return WinFspNative.Status.Success;
        }
        catch (Exception)
        {
            return WinFspNative.Status.InternalError;
        }
    }

    /// <summary>
    /// Renames a file or directory.
    /// </summary>
    public int Rename(
        IntPtr fileContext,
        string fileName,
        string newFileName,
        bool replaceIfExists)
    {
        try
        {
            _renameLock.EnterWriteLock();
            try
            {
                var normalizedOld = NormalizePath(fileName);
                var normalizedNew = NormalizePath(newFileName);

                var entry = _fileSystem.GetEntry(normalizedOld);
                if (entry == null)
                    return WinFspNative.Status.ObjectNameNotFound;

                // Check if target exists
                var targetEntry = _fileSystem.GetEntry(normalizedNew);
                if (targetEntry != null)
                {
                    if (!replaceIfExists)
                        return WinFspNative.Status.ObjectNameCollision;

                    // Can't replace directory with file or vice versa
                    if (targetEntry.IsDirectory != entry.IsDirectory)
                        return WinFspNative.Status.AccessDenied;

                    // Can't replace non-empty directory
                    if (targetEntry.IsDirectory && _fileSystem.HasChildren(normalizedNew))
                        return WinFspNative.Status.DirectoryNotEmpty;

                    // Delete target
                    _fileSystem.DeleteEntry(normalizedNew);
                    _securityHandler.RemoveSecurityDescriptor(normalizedNew);
                }

                // Ensure target parent exists
                var targetParent = GetParentPath(normalizedNew);
                if (!string.IsNullOrEmpty(targetParent) && !_fileSystem.PathExists(targetParent))
                    return WinFspNative.Status.ObjectPathNotFound;

                // Perform rename
                _fileSystem.RenameEntry(normalizedOld, normalizedNew);

                // Move security descriptor
                _securityHandler.CopySecurityDescriptor(normalizedOld, normalizedNew);
                _securityHandler.RemoveSecurityDescriptor(normalizedOld);

                // Invalidate caches
                _cacheManager.InvalidateReadCache(normalizedOld);
                _cacheManager.InvalidateReadCache(normalizedNew);

                // Update open file contexts
                foreach (var kvp in _openFiles)
                {
                    if (kvp.Value.Path.Equals(normalizedOld, StringComparison.OrdinalIgnoreCase))
                    {
                        kvp.Value.Path = normalizedNew;
                    }
                    else if (kvp.Value.Path.StartsWith(normalizedOld + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        kvp.Value.Path = normalizedNew + kvp.Value.Path.Substring(normalizedOld.Length);
                    }
                }

                return WinFspNative.Status.Success;
            }
            finally
            {
                _renameLock.ExitWriteLock();
            }
        }
        catch (Exception)
        {
            return WinFspNative.Status.InternalError;
        }
    }

    #endregion

    #region File Information Operations

    /// <summary>
    /// Gets file information.
    /// </summary>
    public int GetFileInfo(IntPtr fileContext, out WinFspNative.FileInfo fileInfo)
    {
        fileInfo = default;

        try
        {
            if (!_openFiles.TryGetValue(fileContext, out var context))
                return WinFspNative.Status.FileClosed;

            var entry = _fileSystem.GetEntry(context.Path);
            if (entry == null)
                return WinFspNative.Status.ObjectNameNotFound;

            fileInfo = CreateFileInfo(entry);
            return WinFspNative.Status.Success;
        }
        catch (Exception)
        {
            return WinFspNative.Status.InternalError;
        }
    }

    /// <summary>
    /// Sets basic file information (attributes and timestamps).
    /// </summary>
    public int SetBasicInfo(
        IntPtr fileContext,
        uint fileAttributes,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime,
        ulong changeTime,
        out WinFspNative.FileInfo fileInfo)
    {
        fileInfo = default;

        try
        {
            if (!_openFiles.TryGetValue(fileContext, out var context))
                return WinFspNative.Status.FileClosed;

            var entry = _fileSystem.GetEntry(context.Path);
            if (entry == null)
                return WinFspNative.Status.ObjectNameNotFound;

            // Update attributes (preserve directory flag)
            if (fileAttributes != uint.MaxValue)
            {
                var newAttrs = (WinFspNative.FileAttributes)fileAttributes;
                if (entry.IsDirectory)
                    newAttrs |= WinFspNative.FileAttributes.Directory;
                entry.Attributes = newAttrs;
            }

            // Update timestamps (0 = don't change)
            if (creationTime != 0)
                entry.CreationTime = creationTime;
            if (lastAccessTime != 0)
                entry.LastAccessTime = lastAccessTime;
            if (lastWriteTime != 0)
                entry.LastWriteTime = lastWriteTime;
            if (changeTime != 0)
                entry.ChangeTime = changeTime;

            fileInfo = CreateFileInfo(entry);
            return WinFspNative.Status.Success;
        }
        catch (Exception)
        {
            return WinFspNative.Status.InternalError;
        }
    }

    /// <summary>
    /// Sets file size.
    /// </summary>
    public int SetFileSize(
        IntPtr fileContext,
        ulong newSize,
        bool setAllocationSize,
        out WinFspNative.FileInfo fileInfo)
    {
        fileInfo = default;

        try
        {
            if (!_openFiles.TryGetValue(fileContext, out var context))
                return WinFspNative.Status.FileClosed;

            var entry = _fileSystem.GetEntry(context.Path);
            if (entry == null)
                return WinFspNative.Status.ObjectNameNotFound;

            if (setAllocationSize)
            {
                entry.AllocationSize = newSize;
                if ((long)newSize < entry.FileSize)
                {
                    entry.FileSize = (long)newSize;
                    _cacheManager.InvalidateReadCache(context.Path);
                }
            }
            else
            {
                entry.FileSize = (long)newSize;
                if (newSize > entry.AllocationSize)
                {
                    entry.AllocationSize = newSize;
                }
                _cacheManager.InvalidateReadCache(context.Path);
            }

            entry.ChangeTime = WinFspNative.GetCurrentFileTime();
            fileInfo = CreateFileInfo(entry);

            return WinFspNative.Status.Success;
        }
        catch (Exception)
        {
            return WinFspNative.Status.InternalError;
        }
    }

    #endregion

    #region Directory Operations

    /// <summary>
    /// Reads directory contents.
    /// </summary>
    public int ReadDirectory(
        IntPtr fileContext,
        string? pattern,
        string? marker,
        IntPtr buffer,
        uint length,
        out uint bytesTransferred)
    {
        bytesTransferred = 0;

        try
        {
            if (!_openFiles.TryGetValue(fileContext, out var context))
                return WinFspNative.Status.FileClosed;

            if (!context.IsDirectory)
                return WinFspNative.Status.NotADirectory;

            var entries = _fileSystem.ListDirectory(context.Path, pattern);
            var addedAny = false;
            var pastMarker = string.IsNullOrEmpty(marker);

            foreach (var entry in entries)
            {
                var fileName = Path.GetFileName(entry.Path);

                if (!pastMarker)
                {
                    if (fileName.Equals(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        pastMarker = true;
                    }
                    continue;
                }

                var dirInfo = new WinFspNative.DirInfo
                {
                    Size = (ushort)Marshal.SizeOf<WinFspNative.DirInfo>(),
                    FileInfo = CreateFileInfo(entry),
                    FileName = fileName
                };

                if (WinFspNative.FspFileSystemAddDirInfo(ref dirInfo, buffer, length, out bytesTransferred))
                {
                    addedAny = true;
                }
                else
                {
                    break; // Buffer full
                }
            }

            // End enumeration
            if (addedAny)
            {
                WinFspNative.FspFileSystemEndDirInfo(buffer, length, out bytesTransferred);
            }

            return addedAny ? WinFspNative.Status.Success : WinFspNative.Status.NoMoreFiles;
        }
        catch (Exception)
        {
            return WinFspNative.Status.InternalError;
        }
    }

    #endregion

    #region Helper Methods

    private IntPtr AllocateFileHandle()
    {
        return new IntPtr(Interlocked.Increment(ref _nextFileHandle));
    }

    private static ulong GenerateIndexNumber()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return (ulong)DateTime.UtcNow.Ticks ^ BitConverter.ToUInt64(bytes);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "\\";

        path = path.Replace('/', '\\');
        if (!path.StartsWith("\\"))
            path = "\\" + path;

        // Remove trailing backslash (except for root)
        if (path.Length > 1 && path.EndsWith("\\"))
            path = path.TrimEnd('\\');

        return path;
    }

    private static string? GetParentPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "\\")
            return null;

        var lastSep = path.LastIndexOf('\\');
        if (lastSep <= 0)
            return "\\";

        return path.Substring(0, lastSep);
    }

    private static bool ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        // Check for invalid characters
        var invalid = Path.GetInvalidPathChars();
        foreach (var c in path)
        {
            if (Array.IndexOf(invalid, c) >= 0 && c != '\\' && c != '/')
                return false;
        }

        // Check for reserved names
        var fileName = Path.GetFileName(path);
        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        if (reserved.Contains(nameWithoutExt, StringComparer.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private bool CheckAccessForCreate(string path, uint accessMask)
    {
        if (!_securityHandler.EnforceAcls)
            return true;

        var parentPath = GetParentPath(path);
        if (string.IsNullOrEmpty(parentPath))
            return true;

        using var identity = WindowsIdentity.GetCurrent();
        var writeAccess = (uint)(WinFspNative.AccessRights.FileWriteData | WinFspNative.AccessRights.FileAppendData);
        return _securityHandler.CheckAccess(parentPath, identity, writeAccess, true);
    }

    private bool CheckAccess(string path, uint accessMask, bool isDirectory)
    {
        if (!_securityHandler.EnforceAcls)
            return true;

        using var identity = WindowsIdentity.GetCurrent();
        return _securityHandler.CheckAccess(path, identity, accessMask, isDirectory);
    }

    private static WinFspNative.FileInfo CreateFileInfo(FileEntry entry)
    {
        return new WinFspNative.FileInfo
        {
            FileAttributes = entry.Attributes,
            ReparseTag = 0,
            AllocationSize = entry.AllocationSize,
            FileSize = (ulong)entry.FileSize,
            CreationTime = entry.CreationTime,
            LastAccessTime = entry.LastAccessTime,
            LastWriteTime = entry.LastWriteTime,
            ChangeTime = entry.ChangeTime,
            IndexNumber = entry.IndexNumber,
            HardLinks = 1,
            EaSize = 0
        };
    }

    private static byte[] CopySecurityDescriptor(IntPtr sdPtr)
    {
        // Get security descriptor length
        var length = GetSecurityDescriptorLength(sdPtr);
        if (length == 0)
            return Array.Empty<byte>();

        var bytes = new byte[length];
        Marshal.Copy(sdPtr, bytes, 0, (int)length);
        return bytes;
    }

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr pSecurityDescriptor);

    private void PerformPendingWrites(string path, List<WriteChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            _fileSystem.WriteData(path, chunk.Offset, chunk.Data);
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the operations handler.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        // Close all open files
        foreach (var kvp in _openFiles)
        {
            Close(kvp.Key);
        }

        _operationSemaphore.Dispose();
        _renameLock.Dispose();
        _disposed = true;
    }

    #endregion
}

/// <summary>
/// Context for an open file handle.
/// </summary>
internal sealed class OpenFileContext
{
    public required string Path { get; set; }
    public required bool IsDirectory { get; init; }
    public required uint GrantedAccess { get; init; }
    public required bool DeleteOnClose { get; init; }
}

/// <summary>
/// Represents a file or directory entry in the filesystem.
/// </summary>
public sealed record FileEntry
{
    /// <summary>
    /// Full path of the entry.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Whether this is a directory.
    /// </summary>
    public required bool IsDirectory { get; init; }

    /// <summary>
    /// File attributes.
    /// </summary>
    public WinFspNative.FileAttributes Attributes { get; set; }

    /// <summary>
    /// Allocated size on disk.
    /// </summary>
    public ulong AllocationSize { get; set; }

    /// <summary>
    /// Actual file size.
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// Creation time as FILETIME.
    /// </summary>
    public ulong CreationTime { get; set; }

    /// <summary>
    /// Last access time as FILETIME.
    /// </summary>
    public ulong LastAccessTime { get; set; }

    /// <summary>
    /// Last write time as FILETIME.
    /// </summary>
    public ulong LastWriteTime { get; set; }

    /// <summary>
    /// Change time as FILETIME.
    /// </summary>
    public ulong ChangeTime { get; set; }

    /// <summary>
    /// Unique index number.
    /// </summary>
    public ulong IndexNumber { get; set; }
}
