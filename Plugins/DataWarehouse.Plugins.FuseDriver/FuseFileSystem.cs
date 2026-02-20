// <copyright file="FuseFileSystem.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// Licensed under the MIT License.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.FuseDriver;

/// <summary>
/// Core FUSE filesystem implementation.
/// Maps FUSE operations to DataWarehouse storage operations with POSIX semantics.
/// </summary>
public sealed class FuseFileSystem : IDisposable
{
    private readonly FuseConfig _config;
    private readonly IKernelContext? _kernelContext;
    private readonly IStorageProvider? _storageProvider;
    private readonly FuseCacheManager _cacheManager;
    private readonly ExtendedAttributes _xattrStore;
    private readonly ConcurrentDictionary<string, FuseNode> _nodeCache = new();
    private readonly ConcurrentDictionary<ulong, OpenFileHandle> _openHandles = new();
    private readonly ConcurrentDictionary<string, FileLockInfo> _locks = new();
    private readonly PosixPermissions _rootPermissions;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private ulong _nextHandle = 1;
    private ulong _nextInode = 2; // 1 is reserved for root
    private bool _disposed;

    /// <summary>
    /// Gets the root inode number.
    /// </summary>
    public const ulong RootInode = 1;

    /// <summary>
    /// Gets the current user ID of the mounting process.
    /// </summary>
    public uint MountUid { get; private set; }

    /// <summary>
    /// Gets the current group ID of the mounting process.
    /// </summary>
    public uint MountGid { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="FuseFileSystem"/> class.
    /// </summary>
    /// <param name="config">The FUSE configuration.</param>
    /// <param name="kernelContext">Optional kernel context for logging and plugin access.</param>
    public FuseFileSystem(FuseConfig config, IKernelContext? kernelContext = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _kernelContext = kernelContext;
        _cacheManager = new FuseCacheManager(config, kernelContext);
        _xattrStore = new ExtendedAttributes();

        // Try to get storage provider from kernel context
        _storageProvider = kernelContext?.GetPlugin<IStorageProvider>();

        // Set default UID/GID if not specified
        MountUid = _config.DefaultUid != 0 ? _config.DefaultUid : GetCurrentUid();
        MountGid = _config.DefaultGid != 0 ? _config.DefaultGid : GetCurrentGid();

        // Initialize root directory permissions
        _rootPermissions = PosixPermissions.CreateDirectory(_config.DefaultDirMode, MountUid, MountGid);

        // Create root node
        var rootNode = new FuseNode
        {
            Inode = RootInode,
            Path = "/",
            Permissions = _rootPermissions,
            IsDirectory = true,
            Size = 0,
            Atime = DateTime.UtcNow,
            Mtime = DateTime.UtcNow,
            Ctime = DateTime.UtcNow,
            Birthtime = DateTime.UtcNow,
            LinkCount = 2
        };
        _nodeCache["/"] = rootNode;
    }

    #region Core FUSE Operations

    /// <summary>
    /// Gets file attributes.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="stat">The stat structure to fill.</param>
    /// <returns>0 on success, or a negative errno value.</returns>
    public int GetAttr(string path, ref FuseStat stat)
    {
        // Check cache first
        var cachedStat = _cacheManager.GetCachedAttributes(path);
        if (cachedStat.HasValue)
        {
            stat = cachedStat.Value;
            return 0;
        }

        if (!TryGetNode(path, out var node))
        {
            return -FuseErrno.ENOENT;
        }

        stat = NodeToStat(node);
        _cacheManager.CacheAttributes(path, stat);
        return 0;
    }

    /// <summary>
    /// Reads directory contents.
    /// </summary>
    /// <param name="path">The directory path.</param>
    /// <param name="entries">The list to populate with entries.</param>
    /// <returns>0 on success, or a negative errno value.</returns>
    public int ReadDir(string path, List<FuseDirEntry> entries)
    {
        if (!TryGetNode(path, out var node) || !node.IsDirectory)
        {
            return node == null ? -FuseErrno.ENOENT : -FuseErrno.ENOTDIR;
        }

        // Add . and .. entries
        entries.Add(new FuseDirEntry { Name = ".", Ino = node.Inode, Type = FuseDirEntry.DT_DIR });
        entries.Add(new FuseDirEntry { Name = "..", Ino = GetParentInode(path), Type = FuseDirEntry.DT_DIR });

        // Add children
        var prefix = path == "/" ? "/" : path + "/";
        foreach (var kvp in _nodeCache)
        {
            if (kvp.Key != path && kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                var relative = kvp.Key[prefix.Length..];
                if (!relative.Contains('/')) // Direct children only
                {
                    var child = kvp.Value;
                    entries.Add(new FuseDirEntry
                    {
                        Name = relative,
                        Ino = child.Inode,
                        Type = child.IsDirectory ? FuseDirEntry.DT_DIR :
                               child.IsSymlink ? FuseDirEntry.DT_LNK : FuseDirEntry.DT_REG
                    });
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Opens a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="flags">The open flags.</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>0 on success, or a negative errno value.</returns>
    public int Open(string path, OpenFlags flags, ref FuseFileInfo fileInfo)
    {
        if (!TryGetNode(path, out var node))
        {
            return -FuseErrno.ENOENT;
        }

        if (node.IsDirectory)
        {
            return -FuseErrno.EISDIR;
        }

        // Check permissions
        var accessMode = flags & OpenFlags.O_ACCMODE;
        if (!CheckAccess(node, accessMode))
        {
            return -FuseErrno.EACCES;
        }

        // Handle truncate
        if ((flags & OpenFlags.O_TRUNC) != 0)
        {
            node.Size = 0;
            node.Data = Array.Empty<byte>();
            node.Mtime = DateTime.UtcNow;
            node.Ctime = DateTime.UtcNow;
            _cacheManager.InvalidateAttributes(path);
            _cacheManager.InvalidateReadCache(path);
        }

        // Create file handle
        var handle = CreateHandle(node, flags);
        fileInfo.Fh = handle.Handle;

        if (_config.DirectIO)
        {
            fileInfo.DirectIo = 1;
        }

        if (_config.KernelCache)
        {
            fileInfo.KeepCache = 1;
        }

        // Update access time
        node.Atime = DateTime.UtcNow;

        return 0;
    }

    /// <summary>
    /// Reads data from a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="buffer">The buffer to read into.</param>
    /// <param name="size">The number of bytes to read.</param>
    /// <param name="offset">The offset to read from.</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>The number of bytes read, or a negative errno value.</returns>
    public int Read(string path, Span<byte> buffer, long size, long offset, ref FuseFileInfo fileInfo)
    {
        if (!_openHandles.TryGetValue(fileInfo.Fh, out var handle) || handle.Node == null)
        {
            return -FuseErrno.EBADF;
        }

        var node = handle.Node;

        // Check for cached data
        var cached = _cacheManager.GetCachedRead(path, offset, (int)Math.Min(size, buffer.Length));
        if (cached != null)
        {
            var bytesToCopy = Math.Min(cached.Length, buffer.Length);
            cached.AsSpan(0, bytesToCopy).CopyTo(buffer);
            return bytesToCopy;
        }

        // Read from node data or storage
        byte[]? data = node.Data;

        if (data == null || data.Length == 0)
        {
            // FUSE API requires synchronous callbacks — cannot be made async at this boundary.
            // Task.Run avoids deadlocks on synchronization-context-bound threads.
            data = Task.Run(() => LoadFromStorageAsync(path)).ConfigureAwait(false).GetAwaiter().GetResult();
            if (data == null)
            {
                return 0; // EOF
            }

            node.Data = data;
        }

        if (offset >= data.Length)
        {
            return 0; // EOF
        }

        var available = data.Length - (int)offset;
        var toRead = (int)Math.Min(Math.Min(size, buffer.Length), available);

        data.AsSpan((int)offset, toRead).CopyTo(buffer);

        // Cache the read
        _cacheManager.CacheRead(path, offset, data.AsSpan((int)offset, toRead).ToArray());

        // Update access time
        node.Atime = DateTime.UtcNow;

        return toRead;
    }

    /// <summary>
    /// Writes data to a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="buffer">The buffer to write from.</param>
    /// <param name="size">The number of bytes to write.</param>
    /// <param name="offset">The offset to write to.</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>The number of bytes written, or a negative errno value.</returns>
    public int Write(string path, ReadOnlySpan<byte> buffer, long size, long offset, ref FuseFileInfo fileInfo)
    {
        if (!_openHandles.TryGetValue(fileInfo.Fh, out var handle) || handle.Node == null)
        {
            return -FuseErrno.EBADF;
        }

        if ((handle.Flags & OpenFlags.O_ACCMODE) == OpenFlags.O_RDONLY)
        {
            return -FuseErrno.EBADF;
        }

        var node = handle.Node;
        var toWrite = (int)Math.Min(size, buffer.Length);

        if (!_writeLock.Wait(TimeSpan.FromSeconds(30)))
        {
            return -FuseErrno.EIO; // Timeout acquiring lock
        }
        try
        {
            // Handle append mode
            if ((handle.Flags & OpenFlags.O_APPEND) != 0)
            {
                offset = node.Size;
            }

            // Ensure data array is large enough
            var requiredSize = (int)offset + toWrite;
            if (node.Data == null || node.Data.Length < requiredSize)
            {
                var newData = new byte[requiredSize];
                if (node.Data != null)
                {
                    Buffer.BlockCopy(node.Data, 0, newData, 0, node.Data.Length);
                }

                node.Data = newData;
            }

            // Copy the data
            buffer[..toWrite].CopyTo(node.Data.AsSpan((int)offset));

            // Update size if needed
            if (offset + toWrite > node.Size)
            {
                node.Size = offset + toWrite;
            }

            // Update timestamps
            node.Mtime = DateTime.UtcNow;
            node.Ctime = DateTime.UtcNow;

            // Invalidate caches
            _cacheManager.InvalidateAttributes(path);
            _cacheManager.InvalidateReadCache(path);

            // Mark handle as dirty for sync
            handle.IsDirty = true;
        }
        finally
        {
            _writeLock.Release();
        }

        return toWrite;
    }

    /// <summary>
    /// Releases (closes) a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>0 on success, or a negative errno value.</returns>
    public int Release(string path, ref FuseFileInfo fileInfo)
    {
        if (!_openHandles.TryRemove(fileInfo.Fh, out var handle))
        {
            return 0; // Already closed
        }

        // Flush dirty data to storage
        if (handle.IsDirty && handle.Node != null)
        {
            // FUSE API requires synchronous callbacks
            Task.Run(() => SaveToStorageAsync(path, handle.Node.Data ?? Array.Empty<byte>())).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        return 0;
    }

    /// <summary>
    /// Creates a new file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="mode">The file mode.</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>0 on success, or a negative errno value.</returns>
    public int Create(string path, uint mode, ref FuseFileInfo fileInfo)
    {
        if (TryGetNode(path, out _))
        {
            return -FuseErrno.EEXIST;
        }

        var parentPath = GetParentPath(path);
        if (!TryGetNode(parentPath, out var parent) || !parent.IsDirectory)
        {
            return parent == null ? -FuseErrno.ENOENT : -FuseErrno.ENOTDIR;
        }

        // Check write permission on parent
        if (!parent.Permissions.CanWrite(MountUid, MountGid))
        {
            return -FuseErrno.EACCES;
        }

        var now = DateTime.UtcNow;
        var node = new FuseNode
        {
            Inode = Interlocked.Increment(ref _nextInode),
            Path = path,
            Permissions = PosixPermissions.CreateFile(mode, MountUid, MountGid),
            IsDirectory = false,
            Size = 0,
            Data = Array.Empty<byte>(),
            Atime = now,
            Mtime = now,
            Ctime = now,
            Birthtime = now,
            LinkCount = 1
        };

        _nodeCache[path] = node;

        // Update parent mtime
        parent.Mtime = now;
        parent.Ctime = now;

        // Create handle
        var handle = CreateHandle(node, OpenFlags.O_RDWR | OpenFlags.O_CREAT);
        fileInfo.Fh = handle.Handle;

        return 0;
    }

    /// <summary>
    /// Deletes a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>0 on success, or a negative errno value.</returns>
    public int Unlink(string path)
    {
        if (!TryGetNode(path, out var node))
        {
            return -FuseErrno.ENOENT;
        }

        if (node.IsDirectory)
        {
            return -FuseErrno.EISDIR;
        }

        var parentPath = GetParentPath(path);
        if (!TryGetNode(parentPath, out var parent))
        {
            return -FuseErrno.ENOENT;
        }

        // Check write permission on parent
        if (!parent.Permissions.CanWrite(MountUid, MountGid))
        {
            return -FuseErrno.EACCES;
        }

        // Remove from cache
        _nodeCache.TryRemove(path, out _);
        _cacheManager.InvalidateAttributes(path);
        _cacheManager.InvalidateReadCache(path);
        _xattrStore.RemoveAllAttributes(path);

        // Update parent
        parent.Mtime = DateTime.UtcNow;
        parent.Ctime = DateTime.UtcNow;

        // Delete from storage
        // FUSE API requires synchronous callbacks
        Task.Run(() => DeleteFromStorageAsync(path)).ConfigureAwait(false).GetAwaiter().GetResult();

        return 0;
    }

    /// <summary>
    /// Creates a directory.
    /// </summary>
    /// <param name="path">The directory path.</param>
    /// <param name="mode">The directory mode.</param>
    /// <returns>0 on success, or a negative errno value.</returns>
    public int MkDir(string path, uint mode)
    {
        if (TryGetNode(path, out _))
        {
            return -FuseErrno.EEXIST;
        }

        var parentPath = GetParentPath(path);
        if (!TryGetNode(parentPath, out var parent) || !parent.IsDirectory)
        {
            return parent == null ? -FuseErrno.ENOENT : -FuseErrno.ENOTDIR;
        }

        if (!parent.Permissions.CanWrite(MountUid, MountGid))
        {
            return -FuseErrno.EACCES;
        }

        var now = DateTime.UtcNow;
        var node = new FuseNode
        {
            Inode = Interlocked.Increment(ref _nextInode),
            Path = path,
            Permissions = PosixPermissions.CreateDirectory(mode, MountUid, MountGid),
            IsDirectory = true,
            Size = 0,
            Atime = now,
            Mtime = now,
            Ctime = now,
            Birthtime = now,
            LinkCount = 2
        };

        _nodeCache[path] = node;
        parent.LinkCount++;
        parent.Mtime = now;
        parent.Ctime = now;

        return 0;
    }

    /// <summary>
    /// Removes an empty directory.
    /// </summary>
    /// <param name="path">The directory path.</param>
    /// <returns>0 on success, or a negative errno value.</returns>
    public int RmDir(string path)
    {
        if (path == "/")
        {
            return -FuseErrno.EBUSY;
        }

        if (!TryGetNode(path, out var node))
        {
            return -FuseErrno.ENOENT;
        }

        if (!node.IsDirectory)
        {
            return -FuseErrno.ENOTDIR;
        }

        // Check if directory is empty
        var prefix = path + "/";
        if (_nodeCache.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return -FuseErrno.ENOTEMPTY;
        }

        var parentPath = GetParentPath(path);
        if (!TryGetNode(parentPath, out var parent))
        {
            return -FuseErrno.ENOENT;
        }

        if (!parent.Permissions.CanWrite(MountUid, MountGid))
        {
            return -FuseErrno.EACCES;
        }

        _nodeCache.TryRemove(path, out _);
        _cacheManager.InvalidateAttributes(path);
        _xattrStore.RemoveAllAttributes(path);

        parent.LinkCount--;
        parent.Mtime = DateTime.UtcNow;
        parent.Ctime = DateTime.UtcNow;

        return 0;
    }

    /// <summary>
    /// Renames a file or directory.
    /// </summary>
    /// <param name="oldPath">The old path.</param>
    /// <param name="newPath">The new path.</param>
    /// <param name="flags">The rename flags.</param>
    /// <returns>0 on success, or a negative errno value.</returns>
    public int Rename(string oldPath, string newPath, uint flags)
    {
        if (!TryGetNode(oldPath, out var node))
        {
            return -FuseErrno.ENOENT;
        }

        var newExists = TryGetNode(newPath, out var existingNode);

        // Handle RENAME_NOREPLACE
        if ((flags & (uint)RenameFlags.RENAME_NOREPLACE) != 0 && newExists)
        {
            return -FuseErrno.EEXIST;
        }

        // Handle RENAME_EXCHANGE
        if ((flags & (uint)RenameFlags.RENAME_EXCHANGE) != 0)
        {
            if (!newExists)
            {
                return -FuseErrno.ENOENT;
            }

            // Exchange the nodes
            node.Path = newPath;
            existingNode!.Path = oldPath;
            _nodeCache[newPath] = node;
            _nodeCache[oldPath] = existingNode;
            _xattrStore.RenamePath(oldPath, "__temp__");
            _xattrStore.RenamePath(newPath, oldPath);
            _xattrStore.RenamePath("__temp__", newPath);
            return 0;
        }

        // Check parent permissions
        var oldParent = GetParentPath(oldPath);
        var newParent = GetParentPath(newPath);

        if (!TryGetNode(oldParent, out var oldParentNode) ||
            !TryGetNode(newParent, out var newParentNode))
        {
            return -FuseErrno.ENOENT;
        }

        if (!oldParentNode.Permissions.CanWrite(MountUid, MountGid) ||
            !newParentNode.Permissions.CanWrite(MountUid, MountGid))
        {
            return -FuseErrno.EACCES;
        }

        // Remove existing target if it exists
        if (newExists)
        {
            if (existingNode!.IsDirectory != node.IsDirectory)
            {
                return node.IsDirectory ? -FuseErrno.ENOTDIR : -FuseErrno.EISDIR;
            }

            if (existingNode.IsDirectory)
            {
                var prefix = newPath + "/";
                if (_nodeCache.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    return -FuseErrno.ENOTEMPTY;
                }
            }

            _nodeCache.TryRemove(newPath, out _);
            _xattrStore.RemoveAllAttributes(newPath);
        }

        // Move the node
        _nodeCache.TryRemove(oldPath, out _);
        node.Path = newPath;
        node.Ctime = DateTime.UtcNow;
        _nodeCache[newPath] = node;

        // Move children if directory
        if (node.IsDirectory)
        {
            var oldPrefix = oldPath + "/";
            var children = _nodeCache.Where(kvp => kvp.Key.StartsWith(oldPrefix, StringComparison.Ordinal)).ToList();
            foreach (var child in children)
            {
                _nodeCache.TryRemove(child.Key, out var childNode);
                if (childNode != null)
                {
                    childNode.Path = newPath + child.Key[oldPath.Length..];
                    _nodeCache[childNode.Path] = childNode;
                    _xattrStore.RenamePath(child.Key, childNode.Path);
                }
            }
        }

        _xattrStore.RenamePath(oldPath, newPath);
        _cacheManager.InvalidateAttributesPrefix(oldPath);
        _cacheManager.InvalidateReadCache(oldPath);

        // Update parent timestamps
        var now = DateTime.UtcNow;
        oldParentNode.Mtime = now;
        oldParentNode.Ctime = now;
        newParentNode.Mtime = now;
        newParentNode.Ctime = now;

        return 0;
    }

    /// <summary>
    /// Changes file permissions.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="mode">The new mode.</param>
    /// <returns>0 on success, or a negative errno value.</returns>
    public int ChMod(string path, uint mode)
    {
        if (!TryGetNode(path, out var node))
        {
            return -FuseErrno.ENOENT;
        }

        // Only owner or root can change permissions
        if (MountUid != 0 && MountUid != node.Permissions.Uid)
        {
            return -FuseErrno.EPERM;
        }

        node.Permissions.SetPermissions(mode);
        node.Ctime = DateTime.UtcNow;
        _cacheManager.InvalidateAttributes(path);

        return 0;
    }

    /// <summary>
    /// Changes file owner.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="uid">The new user ID.</param>
    /// <param name="gid">The new group ID.</param>
    /// <returns>0 on success, or a negative errno value.</returns>
    public int ChOwn(string path, uint uid, uint gid)
    {
        if (!TryGetNode(path, out var node))
        {
            return -FuseErrno.ENOENT;
        }

        // Only root can change ownership
        if (MountUid != 0)
        {
            return -FuseErrno.EPERM;
        }

        if (uid != uint.MaxValue)
        {
            node.Permissions.Uid = uid;
        }

        if (gid != uint.MaxValue)
        {
            node.Permissions.Gid = gid;
        }

        node.Ctime = DateTime.UtcNow;
        _cacheManager.InvalidateAttributes(path);

        return 0;
    }

    /// <summary>
    /// Truncates a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="size">The new size.</param>
    /// <returns>0 on success, or a negative errno value.</returns>
    public int Truncate(string path, long size)
    {
        if (!TryGetNode(path, out var node))
        {
            return -FuseErrno.ENOENT;
        }

        if (node.IsDirectory)
        {
            return -FuseErrno.EISDIR;
        }

        if (!node.Permissions.CanWrite(MountUid, MountGid))
        {
            return -FuseErrno.EACCES;
        }

        if (size < 0)
        {
            return -FuseErrno.EINVAL;
        }

        if (!_writeLock.Wait(TimeSpan.FromSeconds(30)))
        {
            return -FuseErrno.EIO; // Timeout acquiring lock
        }
        try
        {
            if (size == 0)
            {
                node.Data = Array.Empty<byte>();
            }
            else if (node.Data == null || node.Data.Length != size)
            {
                var newData = new byte[size];
                if (node.Data != null)
                {
                    Buffer.BlockCopy(node.Data, 0, newData, 0, (int)Math.Min(node.Data.Length, size));
                }

                node.Data = newData;
            }

            node.Size = size;
            node.Mtime = DateTime.UtcNow;
            node.Ctime = DateTime.UtcNow;
        }
        finally
        {
            _writeLock.Release();
        }

        _cacheManager.InvalidateAttributes(path);
        _cacheManager.InvalidateReadCache(path);

        return 0;
    }

    /// <summary>
    /// Updates file timestamps.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="atime">The new access time.</param>
    /// <param name="mtime">The new modification time.</param>
    /// <returns>0 on success, or a negative errno value.</returns>
    public int UTimeNs(string path, FuseTimeSpec atime, FuseTimeSpec mtime)
    {
        if (!TryGetNode(path, out var node))
        {
            return -FuseErrno.ENOENT;
        }

        // Check ownership or write permission
        if (MountUid != 0 && MountUid != node.Permissions.Uid &&
            !node.Permissions.CanWrite(MountUid, MountGid))
        {
            return -FuseErrno.EACCES;
        }

        var now = DateTime.UtcNow;

        if (atime.Nsec != FuseTimeSpec.UTIME_OMIT)
        {
            node.Atime = atime.Nsec == FuseTimeSpec.UTIME_NOW ? now : atime.ToDateTime();
        }

        if (mtime.Nsec != FuseTimeSpec.UTIME_OMIT)
        {
            node.Mtime = mtime.Nsec == FuseTimeSpec.UTIME_NOW ? now : mtime.ToDateTime();
        }

        node.Ctime = now;
        _cacheManager.InvalidateAttributes(path);

        return 0;
    }

    /// <summary>
    /// Gets filesystem statistics.
    /// </summary>
    /// <param name="path">A path within the filesystem.</param>
    /// <param name="stat">The statvfs structure to fill.</param>
    /// <returns>0 on success, or a negative errno value.</returns>
    public int StatFs(string path, ref FuseStatVfs stat)
    {
        // Calculate some basic statistics
        long totalSize = 0;
        long fileCount = 0;

        foreach (var node in _nodeCache.Values)
        {
            if (!node.IsDirectory)
            {
                totalSize += node.Size;
                fileCount++;
            }
        }

        // Return virtual filesystem statistics
        // In production, these would come from the actual storage backend
        const long totalSpace = 1024L * 1024 * 1024 * 1024; // 1 TB virtual
        var usedSpace = totalSize;
        var freeSpace = totalSpace - usedSpace;

        stat.Bsize = _config.BlockSize;
        stat.Frsize = _config.BlockSize;
        stat.Blocks = (ulong)(totalSpace / _config.BlockSize);
        stat.Bfree = (ulong)(freeSpace / _config.BlockSize);
        stat.Bavail = (ulong)(freeSpace / _config.BlockSize);
        stat.Files = (ulong)fileCount + (ulong)_nodeCache.Count(n => n.Value.IsDirectory);
        stat.Ffree = uint.MaxValue - stat.Files;
        stat.Favail = stat.Ffree;
        stat.Fsid = 0xDADA; // DataWarehouse identifier
        stat.Flag = 0;
        stat.Namemax = 255;

        return 0;
    }

    /// <summary>
    /// Synchronizes file data.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="dataSync">If true, only sync data.</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>0 on success, or a negative errno value.</returns>
    public int FSync(string path, bool dataSync, ref FuseFileInfo fileInfo)
    {
        if (!_openHandles.TryGetValue(fileInfo.Fh, out var handle) || handle.Node == null)
        {
            return -FuseErrno.EBADF;
        }

        if (handle.IsDirty)
        {
            // FUSE API requires synchronous callbacks
            Task.Run(() => SaveToStorageAsync(path, handle.Node.Data ?? Array.Empty<byte>())).ConfigureAwait(false).GetAwaiter().GetResult();
            handle.IsDirty = false;
        }

        return 0;
    }

    /// <summary>
    /// Flushes file data.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>0 on success, or a negative errno value.</returns>
    public int Flush(string path, ref FuseFileInfo fileInfo)
    {
        // Same as fsync for our implementation
        return FSync(path, false, ref fileInfo);
    }

    #endregion

    #region Extended Attributes

    /// <summary>
    /// Gets an extended attribute.
    /// </summary>
    public int GetXAttr(string path, string name, Span<byte> value, long size)
    {
        if (!TryGetNode(path, out _))
        {
            return -FuseErrno.ENOENT;
        }

        if (!ExtendedAttributes.CheckAccess(name, MountUid, false))
        {
            return -FuseErrno.EACCES;
        }

        var data = _xattrStore.GetAttribute(path, name);
        if (data == null)
        {
            return -FuseErrno.ENODATA;
        }

        if (size == 0)
        {
            return data.Length;
        }

        if (value.Length < data.Length)
        {
            return -FuseErrno.ERANGE;
        }

        data.CopyTo(value);
        return data.Length;
    }

    /// <summary>
    /// Sets an extended attribute.
    /// </summary>
    public int SetXAttr(string path, string name, ReadOnlySpan<byte> value, XattrFlags flags)
    {
        if (!TryGetNode(path, out var node))
        {
            return -FuseErrno.ENOENT;
        }

        if (!ExtendedAttributes.CheckAccess(name, MountUid, true))
        {
            return -FuseErrno.EACCES;
        }

        if (!node.Permissions.CanWrite(MountUid, MountGid))
        {
            return -FuseErrno.EACCES;
        }

        var result = _xattrStore.SetAttribute(path, name, value.ToArray(), flags);
        if (result == 0)
        {
            node.Ctime = DateTime.UtcNow;
        }

        return result;
    }

    /// <summary>
    /// Lists extended attributes.
    /// </summary>
    public int ListXAttr(string path, Span<byte> list, long size)
    {
        if (!TryGetNode(path, out _))
        {
            return -FuseErrno.ENOENT;
        }

        var buffer = _xattrStore.ListAttributesBuffer(path);

        if (size == 0)
        {
            return buffer.Length;
        }

        if (list.Length < buffer.Length)
        {
            return -FuseErrno.ERANGE;
        }

        buffer.CopyTo(list);
        return buffer.Length;
    }

    /// <summary>
    /// Removes an extended attribute.
    /// </summary>
    public int RemoveXAttr(string path, string name)
    {
        if (!TryGetNode(path, out var node))
        {
            return -FuseErrno.ENOENT;
        }

        if (!ExtendedAttributes.CheckAccess(name, MountUid, true))
        {
            return -FuseErrno.EACCES;
        }

        var result = _xattrStore.RemoveAttribute(path, name);
        if (result == 0)
        {
            node.Ctime = DateTime.UtcNow;
        }

        return result;
    }

    #endregion

    #region Symbolic Links

    /// <summary>
    /// Creates a symbolic link.
    /// </summary>
    public int SymLink(string target, string link)
    {
        if (TryGetNode(link, out _))
        {
            return -FuseErrno.EEXIST;
        }

        var parentPath = GetParentPath(link);
        if (!TryGetNode(parentPath, out var parent) || !parent.IsDirectory)
        {
            return parent == null ? -FuseErrno.ENOENT : -FuseErrno.ENOTDIR;
        }

        if (!parent.Permissions.CanWrite(MountUid, MountGid))
        {
            return -FuseErrno.EACCES;
        }

        var now = DateTime.UtcNow;
        var node = new FuseNode
        {
            Inode = Interlocked.Increment(ref _nextInode),
            Path = link,
            Permissions = PosixPermissions.CreateSymlink(MountUid, MountGid),
            IsDirectory = false,
            IsSymlink = true,
            SymlinkTarget = target,
            Size = Encoding.UTF8.GetByteCount(target),
            Atime = now,
            Mtime = now,
            Ctime = now,
            Birthtime = now,
            LinkCount = 1
        };

        _nodeCache[link] = node;
        parent.Mtime = now;
        parent.Ctime = now;

        return 0;
    }

    /// <summary>
    /// Reads a symbolic link target.
    /// </summary>
    public int ReadLink(string path, Span<byte> buffer, long size)
    {
        if (!TryGetNode(path, out var node))
        {
            return -FuseErrno.ENOENT;
        }

        if (!node.IsSymlink)
        {
            return -FuseErrno.EINVAL;
        }

        var target = node.SymlinkTarget ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(target);

        var toCopy = Math.Min(bytes.Length, buffer.Length - 1);
        bytes.AsSpan(0, toCopy).CopyTo(buffer);
        buffer[toCopy] = 0; // Null terminator

        return 0;
    }

    /// <summary>
    /// Creates a hard link.
    /// </summary>
    public int Link(string oldPath, string newPath)
    {
        if (!TryGetNode(oldPath, out var node))
        {
            return -FuseErrno.ENOENT;
        }

        if (node.IsDirectory)
        {
            return -FuseErrno.EPERM;
        }

        if (TryGetNode(newPath, out _))
        {
            return -FuseErrno.EEXIST;
        }

        var parentPath = GetParentPath(newPath);
        if (!TryGetNode(parentPath, out var parent) || !parent.IsDirectory)
        {
            return parent == null ? -FuseErrno.ENOENT : -FuseErrno.ENOTDIR;
        }

        if (!parent.Permissions.CanWrite(MountUid, MountGid))
        {
            return -FuseErrno.EACCES;
        }

        // Create hard link (same node, different path)
        node.LinkCount++;
        _nodeCache[newPath] = node;

        var now = DateTime.UtcNow;
        node.Ctime = now;
        parent.Mtime = now;
        parent.Ctime = now;

        return 0;
    }

    #endregion

    #region Access Control

    /// <summary>
    /// Checks file access permissions.
    /// </summary>
    public int Access(string path, int mask)
    {
        if (!TryGetNode(path, out var node))
        {
            return -FuseErrno.ENOENT;
        }

        // F_OK - existence check
        if (mask == 0)
        {
            return 0;
        }

        var perm = node.Permissions;

        if ((mask & 4) != 0 && !perm.CanRead(MountUid, MountGid)) // R_OK
        {
            return -FuseErrno.EACCES;
        }

        if ((mask & 2) != 0 && !perm.CanWrite(MountUid, MountGid)) // W_OK
        {
            return -FuseErrno.EACCES;
        }

        if ((mask & 1) != 0 && !perm.CanExecute(MountUid, MountGid)) // X_OK
        {
            return -FuseErrno.EACCES;
        }

        return 0;
    }

    #endregion

    #region Fallocate and Sparse Files

    /// <summary>
    /// Allocates or deallocates space for a file (fallocate).
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="mode">The fallocate mode flags.</param>
    /// <param name="offset">The starting offset.</param>
    /// <param name="length">The length of the region.</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>0 on success, or a negative errno value.</returns>
    public int Fallocate(string path, FallocateMode mode, long offset, long length, ref FuseFileInfo fileInfo)
    {
        if (!TryGetNode(path, out var node))
        {
            return -FuseErrno.ENOENT;
        }

        if (node.IsDirectory)
        {
            return -FuseErrno.EISDIR;
        }

        if (!node.Permissions.CanWrite(MountUid, MountGid))
        {
            return -FuseErrno.EACCES;
        }

        if (offset < 0 || length <= 0)
        {
            return -FuseErrno.EINVAL;
        }

        if (!_writeLock.Wait(TimeSpan.FromSeconds(30)))
        {
            return -FuseErrno.EIO; // Timeout acquiring lock
        }
        try
        {
            // Handle punch hole - deallocate space
            if ((mode & FallocateMode.PunchHole) != 0)
            {
                return PunchHole(node, offset, length);
            }

            // Handle zero range - zero out without deallocating
            if ((mode & FallocateMode.ZeroRange) != 0)
            {
                return ZeroRange(node, offset, length, (mode & FallocateMode.KeepSize) != 0);
            }

            // Handle collapse range - remove data and shift
            if ((mode & FallocateMode.CollapseRange) != 0)
            {
                return CollapseRange(node, offset, length);
            }

            // Handle insert range - insert hole and shift data
            if ((mode & FallocateMode.InsertRange) != 0)
            {
                return InsertRange(node, offset, length);
            }

            // Default: preallocate space
            return PreallocateSpace(node, offset, length, (mode & FallocateMode.KeepSize) != 0);
        }
        finally
        {
            _writeLock.Release();
            _cacheManager.InvalidateAttributes(path);
            _cacheManager.InvalidateReadCache(path);
        }
    }

    /// <summary>
    /// Seeks to a hole or data region in a sparse file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="offset">The starting offset.</param>
    /// <param name="whence">SEEK_HOLE or SEEK_DATA.</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>The new offset, or a negative errno value.</returns>
    public long LSeek(string path, long offset, SeekWhence whence, ref FuseFileInfo fileInfo)
    {
        if (!TryGetNode(path, out var node))
        {
            return -FuseErrno.ENOENT;
        }

        if (node.IsDirectory)
        {
            return -FuseErrno.EISDIR;
        }

        if (offset < 0)
        {
            return -FuseErrno.EINVAL;
        }

        // Get sparse regions
        var sparseRegions = GetSparseRegions(node);

        switch (whence)
        {
            case SeekWhence.SeekHole:
                return FindNextHole(node, offset, sparseRegions);

            case SeekWhence.SeekData:
                return FindNextData(node, offset, sparseRegions);

            default:
                return -FuseErrno.EINVAL;
        }
    }

    /// <summary>
    /// Gets the actual blocks allocated for a file (for sparse file support).
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The number of 512-byte blocks actually allocated.</returns>
    public long GetAllocatedBlocks(string path)
    {
        if (!TryGetNode(path, out var node))
        {
            return 0;
        }

        // Calculate actual allocated blocks based on sparse regions
        var sparseRegions = GetSparseRegions(node);
        var totalHoleSize = sparseRegions.Sum(r => r.Length);
        var allocatedBytes = node.Size - totalHoleSize;

        // Return in 512-byte blocks
        return (allocatedBytes + 511) / 512;
    }

    private int PunchHole(FuseNode node, long offset, long length)
    {
        // Punch hole requires the range to be within file bounds
        if (offset >= node.Size)
        {
            return 0; // Nothing to do
        }

        var end = Math.Min(offset + length, node.Size);
        var actualLength = end - offset;

        if (actualLength <= 0)
        {
            return 0;
        }

        // Zero out the region (in a real implementation, this would mark as sparse)
        if (node.Data != null && offset < node.Data.Length)
        {
            var zeroEnd = (int)Math.Min(end, node.Data.Length);
            Array.Clear(node.Data, (int)offset, zeroEnd - (int)offset);
        }

        // Track sparse region
        AddSparseRegion(node, offset, actualLength);

        node.Ctime = DateTime.UtcNow;
        return 0;
    }

    private int ZeroRange(FuseNode node, long offset, long length, bool keepSize)
    {
        var end = offset + length;

        // Extend file if necessary (unless keep_size)
        if (!keepSize && end > node.Size)
        {
            node.Size = end;
        }

        // Zero out the region
        EnsureDataCapacity(node, (int)Math.Min(end, node.Size));

        if (node.Data != null)
        {
            var clearEnd = (int)Math.Min(end, node.Data.Length);
            if (offset < clearEnd)
            {
                Array.Clear(node.Data, (int)offset, clearEnd - (int)offset);
            }
        }

        node.Mtime = DateTime.UtcNow;
        node.Ctime = DateTime.UtcNow;
        return 0;
    }

    private int CollapseRange(FuseNode node, long offset, long length)
    {
        // Collapse must be block-aligned in real implementations
        if (offset + length > node.Size)
        {
            return -FuseErrno.EINVAL;
        }

        if (node.Data != null && offset < node.Data.Length)
        {
            var newSize = node.Data.Length - (int)length;
            var newData = new byte[newSize];

            // Copy data before the collapsed region
            if (offset > 0)
            {
                Buffer.BlockCopy(node.Data, 0, newData, 0, (int)offset);
            }

            // Copy data after the collapsed region
            var afterOffset = (int)(offset + length);
            if (afterOffset < node.Data.Length)
            {
                Buffer.BlockCopy(node.Data, afterOffset, newData, (int)offset, node.Data.Length - afterOffset);
            }

            node.Data = newData;
        }

        node.Size -= length;
        node.Mtime = DateTime.UtcNow;
        node.Ctime = DateTime.UtcNow;
        return 0;
    }

    private int InsertRange(FuseNode node, long offset, long length)
    {
        // Insert range must be within file bounds
        if (offset > node.Size)
        {
            return -FuseErrno.EINVAL;
        }

        var newSize = node.Size + length;
        var newData = new byte[newSize];

        if (node.Data != null)
        {
            // Copy data before the insertion point
            if (offset > 0)
            {
                Buffer.BlockCopy(node.Data, 0, newData, 0, (int)Math.Min(offset, node.Data.Length));
            }

            // Copy data after the insertion point (shifted)
            if (offset < node.Data.Length)
            {
                Buffer.BlockCopy(node.Data, (int)offset, newData, (int)(offset + length), node.Data.Length - (int)offset);
            }
        }

        // The inserted region is zeros (hole)
        node.Data = newData;
        node.Size = newSize;

        // Track the inserted hole as sparse
        AddSparseRegion(node, offset, length);

        node.Mtime = DateTime.UtcNow;
        node.Ctime = DateTime.UtcNow;
        return 0;
    }

    private int PreallocateSpace(FuseNode node, long offset, long length, bool keepSize)
    {
        var end = offset + length;

        // Extend file if necessary (unless keep_size)
        if (!keepSize && end > node.Size)
        {
            node.Size = end;
        }

        // Ensure data array is large enough
        EnsureDataCapacity(node, (int)Math.Min(end, node.Size));

        // Remove any sparse regions in this range (space is now allocated)
        RemoveSparseRegions(node, offset, length);

        node.Ctime = DateTime.UtcNow;
        return 0;
    }

    private void EnsureDataCapacity(FuseNode node, int requiredSize)
    {
        if (node.Data == null || node.Data.Length < requiredSize)
        {
            var newData = new byte[requiredSize];
            if (node.Data != null)
            {
                Buffer.BlockCopy(node.Data, 0, newData, 0, node.Data.Length);
            }
            node.Data = newData;
        }
    }

    private List<SparseRegion> GetSparseRegions(FuseNode node)
    {
        // In a real implementation, this would be stored in the node
        // For now, return empty list (no sparse tracking yet)
        return new List<SparseRegion>();
    }

    private void AddSparseRegion(FuseNode node, long offset, long length)
    {
        // In a real implementation, track sparse regions per node
        _kernelContext?.LogDebug($"Added sparse region at {offset} length {length}");
    }

    private void RemoveSparseRegions(FuseNode node, long offset, long length)
    {
        // In a real implementation, remove sparse regions that overlap
        _kernelContext?.LogDebug($"Removed sparse regions at {offset} length {length}");
    }

    private long FindNextHole(FuseNode node, long offset, List<SparseRegion> sparseRegions)
    {
        // If offset is past end of file, return end of file (implicit hole)
        if (offset >= node.Size)
        {
            return node.Size;
        }

        // Check if we're already in a hole
        foreach (var region in sparseRegions.OrderBy(r => r.Offset))
        {
            if (offset >= region.Offset && offset < region.Offset + region.Length)
            {
                // Already in a hole
                return offset;
            }

            if (region.Offset > offset)
            {
                // Found next hole
                return region.Offset;
            }
        }

        // No holes found, return end of file (implicit hole after EOF)
        return node.Size;
    }

    private long FindNextData(FuseNode node, long offset, List<SparseRegion> sparseRegions)
    {
        // If offset is past end of file, error
        if (offset >= node.Size)
        {
            return -FuseErrno.ENXIO;
        }

        // Check sparse regions
        foreach (var region in sparseRegions.OrderBy(r => r.Offset))
        {
            if (offset >= region.Offset && offset < region.Offset + region.Length)
            {
                // Currently in a hole, find end of hole
                var holeEnd = region.Offset + region.Length;
                if (holeEnd >= node.Size)
                {
                    return -FuseErrno.ENXIO; // No more data
                }
                return holeEnd;
            }
        }

        // Not in a hole, return current offset
        return offset;
    }

    #endregion

    #region File Locking

    /// <summary>
    /// Tests or sets a file lock.
    /// </summary>
    public int Lock(string path, ref FuseFileInfo fileInfo, int command, ref FuseFlock flock)
    {
        const int F_GETLK = 5;
        const int F_SETLK = 6;
        const int F_SETLKW = 7;

        var lockKey = $"{path}:{flock.Start}:{flock.Len}";

        switch (command)
        {
            case F_GETLK:
                // Test lock
                if (_locks.TryGetValue(lockKey, out var existingLock))
                {
                    flock.Type = existingLock.Type;
                    flock.Pid = existingLock.Pid;
                }
                else
                {
                    flock.Type = FuseFlock.F_UNLCK;
                }

                return 0;

            case F_SETLK:
            case F_SETLKW:
                if (flock.Type == FuseFlock.F_UNLCK)
                {
                    _locks.TryRemove(lockKey, out _);
                }
                else
                {
                    if (_locks.ContainsKey(lockKey) && command == F_SETLK)
                    {
                        return -FuseErrno.EAGAIN;
                    }

                    _locks[lockKey] = new FileLockInfo
                    {
                        Type = flock.Type,
                        Start = flock.Start,
                        Length = flock.Len,
                        Pid = Environment.ProcessId
                    };
                }

                return 0;

            default:
                return -FuseErrno.EINVAL;
        }
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Initializes the filesystem.
    /// </summary>
    public nint Init(ref FuseConnInfo conn)
    {
        // Request capabilities
        if (_config.WritebackCache)
        {
            conn.Want |= 0x00010000; // FUSE_CAP_WRITEBACK_CACHE
        }

        if (_config.SpliceRead)
        {
            conn.Want |= 0x00000080; // FUSE_CAP_SPLICE_READ
        }

        if (_config.SpliceWrite)
        {
            conn.Want |= 0x00000100; // FUSE_CAP_SPLICE_WRITE
        }

        conn.MaxWrite = _config.MaxWrite;
        conn.MaxRead = _config.MaxRead;
        conn.MaxBackground = (ushort)_config.MaxBackground;
        conn.CongestionThreshold = (ushort)_config.CongestionThreshold;

        _kernelContext?.LogInfo($"FUSE filesystem initialized (protocol {conn.ProtoMajor}.{conn.ProtoMinor})");

        return nint.Zero;
    }

    /// <summary>
    /// Destroys the filesystem.
    /// </summary>
    public void Destroy(nint privateData)
    {
        _kernelContext?.LogInfo("FUSE filesystem shutting down");

        // Flush all dirty handles
        foreach (var handle in _openHandles.Values.Where(h => h.IsDirty && h.Node != null))
        {
            // FUSE API requires synchronous callbacks
            Task.Run(() => SaveToStorageAsync(handle.Node!.Path, handle.Node.Data ?? Array.Empty<byte>())).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        _openHandles.Clear();
        _locks.Clear();
    }

    #endregion

    #region Helper Methods

    private bool TryGetNode(string path, out FuseNode node)
    {
        return _nodeCache.TryGetValue(NormalizePath(path), out node!);
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

    private ulong GetParentInode(string path)
    {
        var parentPath = GetParentPath(path);
        return TryGetNode(parentPath, out var parent) ? parent.Inode : RootInode;
    }

    private FuseStat NodeToStat(FuseNode node)
    {
        return new FuseStat
        {
            Dev = 0,
            Ino = node.Inode,
            Mode = node.Permissions.Mode,
            Nlink = node.LinkCount,
            Uid = node.Permissions.Uid,
            Gid = node.Permissions.Gid,
            Rdev = 0,
            Size = node.Size,
            Blksize = _config.BlockSize,
            Blocks = (node.Size + 511) / 512,
            Atime = new DateTimeOffset(node.Atime).ToUnixTimeSeconds(),
            AtimeNsec = node.Atime.Ticks % TimeSpan.TicksPerSecond * 100,
            Mtime = new DateTimeOffset(node.Mtime).ToUnixTimeSeconds(),
            MtimeNsec = node.Mtime.Ticks % TimeSpan.TicksPerSecond * 100,
            Ctime = new DateTimeOffset(node.Ctime).ToUnixTimeSeconds(),
            CtimeNsec = node.Ctime.Ticks % TimeSpan.TicksPerSecond * 100,
            Birthtime = new DateTimeOffset(node.Birthtime).ToUnixTimeSeconds(),
            BirthtimeNsec = node.Birthtime.Ticks % TimeSpan.TicksPerSecond * 100
        };
    }

    private OpenFileHandle CreateHandle(FuseNode node, OpenFlags flags)
    {
        var handle = new OpenFileHandle
        {
            Handle = Interlocked.Increment(ref _nextHandle),
            Node = node,
            Flags = flags,
            OpenedAt = DateTime.UtcNow
        };
        _openHandles[handle.Handle] = handle;
        return handle;
    }

    private bool CheckAccess(FuseNode node, OpenFlags accessMode)
    {
        var perm = node.Permissions;

        return accessMode switch
        {
            OpenFlags.O_RDONLY => perm.CanRead(MountUid, MountGid),
            OpenFlags.O_WRONLY => perm.CanWrite(MountUid, MountGid),
            OpenFlags.O_RDWR => perm.CanRead(MountUid, MountGid) && perm.CanWrite(MountUid, MountGid),
            _ => true
        };
    }

    private async Task<byte[]?> LoadFromStorageAsync(string path)
    {
        if (_storageProvider == null)
            return null;

        try
        {
            var uri = new Uri($"dw://fuse{path}");
            using var stream = await _storageProvider.LoadAsync(uri);
            using var ms = new MemoryStream(65536);
            await stream.CopyToAsync(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveToStorageAsync(string path, byte[] data)
    {
        if (_storageProvider == null)
            return;

        try
        {
            var uri = new Uri($"dw://fuse{path}");
            using var stream = new MemoryStream(data);
            await _storageProvider.SaveAsync(uri, stream);
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"Failed to save {path} to storage", ex);
        }
    }

    private async Task DeleteFromStorageAsync(string path)
    {
        if (_storageProvider == null)
            return;

        try
        {
            var uri = new Uri($"dw://fuse{path}");
            await _storageProvider.DeleteAsync(uri);
        }
        catch
        {
            // Ignore deletion errors
        }
    }

    private static uint GetCurrentUid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return 0;

        try
        {
            return NativePosix.getuid();
        }
        catch
        {
            return 0;
        }
    }

    private static uint GetCurrentGid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return 0;

        try
        {
            return NativePosix.getgid();
        }
        catch
        {
            return 0;
        }
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

        _disposed = true;
        _cacheManager.Dispose();
        _writeLock.Dispose();
        _openHandles.Clear();
        _nodeCache.Clear();
        _locks.Clear();
    }

    #endregion

    #region Native Interop

    private static class NativePosix
    {
        [DllImport("libc", SetLastError = true)]
        public static extern uint getuid();

        [DllImport("libc", SetLastError = true)]
        public static extern uint getgid();
    }

    #endregion
}

/// <summary>
/// Internal filesystem node representation.
/// </summary>
internal sealed class FuseNode
{
    public ulong Inode { get; set; }
    public string Path { get; set; } = string.Empty;
    public PosixPermissions Permissions { get; set; } = new();
    public bool IsDirectory { get; set; }
    public bool IsSymlink { get; set; }
    public string? SymlinkTarget { get; set; }
    public long Size { get; set; }
    public byte[]? Data { get; set; }
    public DateTime Atime { get; set; }
    public DateTime Mtime { get; set; }
    public DateTime Ctime { get; set; }
    public DateTime Birthtime { get; set; }
    public uint LinkCount { get; set; }
}

/// <summary>
/// Open file handle information.
/// </summary>
internal sealed class OpenFileHandle
{
    public ulong Handle { get; set; }
    public FuseNode? Node { get; set; }
    public OpenFlags Flags { get; set; }
    public DateTime OpenedAt { get; set; }
    public bool IsDirty { get; set; }
}

/// <summary>
/// File lock information.
/// </summary>
internal sealed class FileLockInfo
{
    public short Type { get; set; }
    public long Start { get; set; }
    public long Length { get; set; }
    public int Pid { get; set; }
}

/// <summary>
/// Represents a sparse (hole) region in a file.
/// </summary>
internal sealed class SparseRegion
{
    /// <summary>
    /// Gets or sets the offset of the sparse region.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// Gets or sets the length of the sparse region.
    /// </summary>
    public long Length { get; set; }
}

/// <summary>
/// Fallocate mode flags.
/// </summary>
[Flags]
public enum FallocateMode : uint
{
    /// <summary>
    /// Default allocation mode.
    /// </summary>
    None = 0,

    /// <summary>
    /// Keep the file size unchanged.
    /// </summary>
    KeepSize = 0x01,

    /// <summary>
    /// Punch a hole (deallocate space).
    /// </summary>
    PunchHole = 0x02,

    /// <summary>
    /// No-hide-stale mode (Linux-specific).
    /// </summary>
    NoHideStale = 0x04,

    /// <summary>
    /// Collapse the range, removing it from the file.
    /// </summary>
    CollapseRange = 0x08,

    /// <summary>
    /// Zero the range without deallocating.
    /// </summary>
    ZeroRange = 0x10,

    /// <summary>
    /// Insert a range, shifting existing data.
    /// </summary>
    InsertRange = 0x20,

    /// <summary>
    /// Unshare extent (copy-on-write break).
    /// </summary>
    UnshareRange = 0x40
}

/// <summary>
/// Seek whence values for lseek with SEEK_HOLE/SEEK_DATA.
/// </summary>
public enum SeekWhence
{
    /// <summary>
    /// Seek from beginning of file.
    /// </summary>
    SeekSet = 0,

    /// <summary>
    /// Seek from current position.
    /// </summary>
    SeekCur = 1,

    /// <summary>
    /// Seek from end of file.
    /// </summary>
    SeekEnd = 2,

    /// <summary>
    /// Seek to next data region.
    /// </summary>
    SeekData = 3,

    /// <summary>
    /// Seek to next hole region.
    /// </summary>
    SeekHole = 4
}
