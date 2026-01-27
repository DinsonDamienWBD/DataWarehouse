// <copyright file="FuseOperations.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// Licensed under the MIT License.
// </copyright>

using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.FuseDriver;

/// <summary>
/// FUSE operation handlers implementing fuse_operations struct callbacks.
/// Maps FUSE operations to DataWarehouse storage operations.
/// </summary>
public sealed class FuseOperations
{
    private readonly FuseFileSystem _fs;
    private readonly IKernelContext? _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="FuseOperations"/> class.
    /// </summary>
    /// <param name="fileSystem">The FUSE filesystem implementation.</param>
    /// <param name="context">Optional kernel context for logging and plugin access.</param>
    public FuseOperations(FuseFileSystem fileSystem, IKernelContext? context = null)
    {
        _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _context = context;
    }

    #region File Operations

    /// <summary>
    /// Gets file attributes (stat).
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="stat">The stat structure to fill.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int GetAttr(string path, ref FuseStat stat)
    {
        try
        {
            return _fs.GetAttr(path, ref stat);
        }
        catch (Exception ex)
        {
            LogError($"getattr failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Reads the contents of a directory.
    /// </summary>
    /// <param name="path">The directory path.</param>
    /// <param name="entries">The list to populate with directory entries.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int ReadDir(string path, List<FuseDirEntry> entries)
    {
        try
        {
            return _fs.ReadDir(path, entries);
        }
        catch (Exception ex)
        {
            LogError($"readdir failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Opens a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="flags">The open flags.</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int Open(string path, OpenFlags flags, ref FuseFileInfo fileInfo)
    {
        try
        {
            return _fs.Open(path, flags, ref fileInfo);
        }
        catch (Exception ex)
        {
            LogError($"open failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Reads data from a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="buffer">The buffer to read into.</param>
    /// <param name="size">The number of bytes to read.</param>
    /// <param name="offset">The offset to read from.</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>The number of bytes read, or a negative errno value on failure.</returns>
    public int Read(string path, Span<byte> buffer, long size, long offset, ref FuseFileInfo fileInfo)
    {
        try
        {
            return _fs.Read(path, buffer, size, offset, ref fileInfo);
        }
        catch (Exception ex)
        {
            LogError($"read failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Writes data to a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="buffer">The buffer to write from.</param>
    /// <param name="size">The number of bytes to write.</param>
    /// <param name="offset">The offset to write to.</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>The number of bytes written, or a negative errno value on failure.</returns>
    public int Write(string path, ReadOnlySpan<byte> buffer, long size, long offset, ref FuseFileInfo fileInfo)
    {
        try
        {
            return _fs.Write(path, buffer, size, offset, ref fileInfo);
        }
        catch (Exception ex)
        {
            LogError($"write failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Releases (closes) a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int Release(string path, ref FuseFileInfo fileInfo)
    {
        try
        {
            return _fs.Release(path, ref fileInfo);
        }
        catch (Exception ex)
        {
            LogError($"release failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Creates a new file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="mode">The file mode (permissions).</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int Create(string path, uint mode, ref FuseFileInfo fileInfo)
    {
        try
        {
            return _fs.Create(path, mode, ref fileInfo);
        }
        catch (Exception ex)
        {
            LogError($"create failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Deletes a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int Unlink(string path)
    {
        try
        {
            return _fs.Unlink(path);
        }
        catch (Exception ex)
        {
            LogError($"unlink failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    #endregion

    #region Directory Operations

    /// <summary>
    /// Creates a directory.
    /// </summary>
    /// <param name="path">The directory path.</param>
    /// <param name="mode">The directory mode (permissions).</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int MkDir(string path, uint mode)
    {
        try
        {
            return _fs.MkDir(path, mode);
        }
        catch (Exception ex)
        {
            LogError($"mkdir failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Removes an empty directory.
    /// </summary>
    /// <param name="path">The directory path.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int RmDir(string path)
    {
        try
        {
            return _fs.RmDir(path);
        }
        catch (Exception ex)
        {
            LogError($"rmdir failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Renames a file or directory.
    /// </summary>
    /// <param name="oldPath">The original path.</param>
    /// <param name="newPath">The new path.</param>
    /// <param name="flags">The rename flags (RENAME_NOREPLACE, RENAME_EXCHANGE).</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int Rename(string oldPath, string newPath, uint flags = 0)
    {
        try
        {
            return _fs.Rename(oldPath, newPath, flags);
        }
        catch (Exception ex)
        {
            LogError($"rename failed from {oldPath} to {newPath}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    #endregion

    #region Permission Operations

    /// <summary>
    /// Changes file permissions.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="mode">The new mode (permissions).</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int ChMod(string path, uint mode)
    {
        try
        {
            return _fs.ChMod(path, mode);
        }
        catch (Exception ex)
        {
            LogError($"chmod failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Changes file owner and group.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="uid">The new owner user ID.</param>
    /// <param name="gid">The new owner group ID.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int ChOwn(string path, uint uid, uint gid)
    {
        try
        {
            return _fs.ChOwn(path, uid, gid);
        }
        catch (Exception ex)
        {
            LogError($"chown failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Truncates a file to the specified size.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="size">The new file size.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int Truncate(string path, long size)
    {
        try
        {
            return _fs.Truncate(path, size);
        }
        catch (Exception ex)
        {
            LogError($"truncate failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Updates file timestamps.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="atime">The new access time.</param>
    /// <param name="mtime">The new modification time.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int UTimeNs(string path, FuseTimeSpec atime, FuseTimeSpec mtime)
    {
        try
        {
            return _fs.UTimeNs(path, atime, mtime);
        }
        catch (Exception ex)
        {
            LogError($"utimens failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    #endregion

    #region Filesystem Operations

    /// <summary>
    /// Gets filesystem statistics.
    /// </summary>
    /// <param name="path">A path within the filesystem.</param>
    /// <param name="stat">The statvfs structure to fill.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int StatFs(string path, ref FuseStatVfs stat)
    {
        try
        {
            return _fs.StatFs(path, ref stat);
        }
        catch (Exception ex)
        {
            LogError($"statfs failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Synchronizes file data to disk.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="dataSync">If true, only sync data, not metadata.</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int FSync(string path, bool dataSync, ref FuseFileInfo fileInfo)
    {
        try
        {
            return _fs.FSync(path, dataSync, ref fileInfo);
        }
        catch (Exception ex)
        {
            LogError($"fsync failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Flushes cached data for a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int Flush(string path, ref FuseFileInfo fileInfo)
    {
        try
        {
            return _fs.Flush(path, ref fileInfo);
        }
        catch (Exception ex)
        {
            LogError($"flush failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    #endregion

    #region Extended Attributes

    /// <summary>
    /// Gets an extended attribute.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="name">The attribute name.</param>
    /// <param name="value">The buffer for the attribute value.</param>
    /// <param name="size">The buffer size.</param>
    /// <returns>The attribute size on success, or a negative errno value on failure.</returns>
    public int GetXAttr(string path, string name, Span<byte> value, long size)
    {
        try
        {
            return _fs.GetXAttr(path, name, value, size);
        }
        catch (Exception ex)
        {
            LogError($"getxattr failed for {path}:{name}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Sets an extended attribute.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="name">The attribute name.</param>
    /// <param name="value">The attribute value.</param>
    /// <param name="flags">The operation flags.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int SetXAttr(string path, string name, ReadOnlySpan<byte> value, XattrFlags flags)
    {
        try
        {
            return _fs.SetXAttr(path, name, value, flags);
        }
        catch (Exception ex)
        {
            LogError($"setxattr failed for {path}:{name}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Lists extended attributes.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="list">The buffer for the attribute names.</param>
    /// <param name="size">The buffer size.</param>
    /// <returns>The total size of attribute names on success, or a negative errno value on failure.</returns>
    public int ListXAttr(string path, Span<byte> list, long size)
    {
        try
        {
            return _fs.ListXAttr(path, list, size);
        }
        catch (Exception ex)
        {
            LogError($"listxattr failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Removes an extended attribute.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="name">The attribute name.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int RemoveXAttr(string path, string name)
    {
        try
        {
            return _fs.RemoveXAttr(path, name);
        }
        catch (Exception ex)
        {
            LogError($"removexattr failed for {path}:{name}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    #endregion

    #region Symbolic Links

    /// <summary>
    /// Creates a symbolic link.
    /// </summary>
    /// <param name="target">The link target.</param>
    /// <param name="link">The link path.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int SymLink(string target, string link)
    {
        try
        {
            return _fs.SymLink(target, link);
        }
        catch (Exception ex)
        {
            LogError($"symlink failed from {link} to {target}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Reads the target of a symbolic link.
    /// </summary>
    /// <param name="path">The link path.</param>
    /// <param name="buffer">The buffer for the target path.</param>
    /// <param name="size">The buffer size.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int ReadLink(string path, Span<byte> buffer, long size)
    {
        try
        {
            return _fs.ReadLink(path, buffer, size);
        }
        catch (Exception ex)
        {
            LogError($"readlink failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Creates a hard link.
    /// </summary>
    /// <param name="oldPath">The existing file path.</param>
    /// <param name="newPath">The new link path.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int Link(string oldPath, string newPath)
    {
        try
        {
            return _fs.Link(oldPath, newPath);
        }
        catch (Exception ex)
        {
            LogError($"link failed from {oldPath} to {newPath}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    #endregion

    #region Fallocate and Sparse Files

    /// <summary>
    /// Allocates or deallocates space for a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="mode">The fallocate mode flags.</param>
    /// <param name="offset">The starting offset.</param>
    /// <param name="length">The length of the region.</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    /// <remarks>
    /// Supported modes:
    /// - Default (0): Preallocate space
    /// - FALLOC_FL_KEEP_SIZE: Preallocate without extending file
    /// - FALLOC_FL_PUNCH_HOLE: Deallocate space (create hole)
    /// - FALLOC_FL_COLLAPSE_RANGE: Remove range and shift data
    /// - FALLOC_FL_ZERO_RANGE: Zero out range
    /// - FALLOC_FL_INSERT_RANGE: Insert hole and shift data
    /// </remarks>
    public int Fallocate(string path, FallocateMode mode, long offset, long length, ref FuseFileInfo fileInfo)
    {
        try
        {
            return _fs.Fallocate(path, mode, offset, length, ref fileInfo);
        }
        catch (Exception ex)
        {
            LogError($"fallocate failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    /// <summary>
    /// Seeks to a hole or data region in a sparse file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="offset">The starting offset.</param>
    /// <param name="whence">SEEK_HOLE (4) or SEEK_DATA (3).</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <returns>The new offset on success, or a negative errno value on failure.</returns>
    /// <remarks>
    /// This operation supports sparse file navigation:
    /// - SEEK_HOLE: Find next hole at or after offset
    /// - SEEK_DATA: Find next data region at or after offset
    /// Returns ENXIO if no hole/data found after offset.
    /// </remarks>
    public long LSeek(string path, long offset, SeekWhence whence, ref FuseFileInfo fileInfo)
    {
        try
        {
            return _fs.LSeek(path, offset, whence, ref fileInfo);
        }
        catch (Exception ex)
        {
            LogError($"lseek failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    #endregion

    #region Access Control

    /// <summary>
    /// Checks file access permissions.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="mask">The access mask to check.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int Access(string path, int mask)
    {
        try
        {
            return _fs.Access(path, mask);
        }
        catch (Exception ex)
        {
            LogError($"access failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    #endregion

    #region File Locking

    /// <summary>
    /// Tests or sets a POSIX file lock.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="fileInfo">The file info structure.</param>
    /// <param name="command">The lock command (F_GETLK, F_SETLK, F_SETLKW).</param>
    /// <param name="flock">The lock structure.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int Lock(string path, ref FuseFileInfo fileInfo, int command, ref FuseFlock flock)
    {
        try
        {
            return _fs.Lock(path, ref fileInfo, command, ref flock);
        }
        catch (Exception ex)
        {
            LogError($"lock failed for {path}", ex);
            return FuseErrno.FromException(ex);
        }
    }

    #endregion

    #region Initialization/Cleanup

    /// <summary>
    /// Initializes the filesystem.
    /// </summary>
    /// <param name="conn">The connection info structure.</param>
    /// <returns>Private data pointer for the filesystem.</returns>
    public nint Init(ref FuseConnInfo conn)
    {
        try
        {
            return _fs.Init(ref conn);
        }
        catch (Exception ex)
        {
            LogError("init failed", ex);
            return nint.Zero;
        }
    }

    /// <summary>
    /// Cleans up the filesystem.
    /// </summary>
    /// <param name="privateData">The private data pointer from init.</param>
    public void Destroy(nint privateData)
    {
        try
        {
            _fs.Destroy(privateData);
        }
        catch (Exception ex)
        {
            LogError("destroy failed", ex);
        }
    }

    #endregion

    private void LogError(string message, Exception ex)
    {
        _context?.LogError($"FUSE: {message}", ex);
    }
}

/// <summary>
/// FUSE stat structure for file attributes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FuseStat
{
    /// <summary>
    /// Device ID.
    /// </summary>
    public ulong Dev;

    /// <summary>
    /// Inode number.
    /// </summary>
    public ulong Ino;

    /// <summary>
    /// File mode (type and permissions).
    /// </summary>
    public uint Mode;

    /// <summary>
    /// Number of hard links.
    /// </summary>
    public uint Nlink;

    /// <summary>
    /// User ID of owner.
    /// </summary>
    public uint Uid;

    /// <summary>
    /// Group ID of owner.
    /// </summary>
    public uint Gid;

    /// <summary>
    /// Device ID (if special file).
    /// </summary>
    public ulong Rdev;

    /// <summary>
    /// Total size in bytes.
    /// </summary>
    public long Size;

    /// <summary>
    /// Block size for filesystem I/O.
    /// </summary>
    public long Blksize;

    /// <summary>
    /// Number of 512B blocks allocated.
    /// </summary>
    public long Blocks;

    /// <summary>
    /// Access time (seconds since epoch).
    /// </summary>
    public long Atime;

    /// <summary>
    /// Access time (nanoseconds).
    /// </summary>
    public long AtimeNsec;

    /// <summary>
    /// Modification time (seconds since epoch).
    /// </summary>
    public long Mtime;

    /// <summary>
    /// Modification time (nanoseconds).
    /// </summary>
    public long MtimeNsec;

    /// <summary>
    /// Status change time (seconds since epoch).
    /// </summary>
    public long Ctime;

    /// <summary>
    /// Status change time (nanoseconds).
    /// </summary>
    public long CtimeNsec;

    /// <summary>
    /// Birth time (seconds since epoch) - macOS only.
    /// </summary>
    public long Birthtime;

    /// <summary>
    /// Birth time (nanoseconds) - macOS only.
    /// </summary>
    public long BirthtimeNsec;
}

/// <summary>
/// FUSE directory entry.
/// </summary>
public struct FuseDirEntry
{
    /// <summary>
    /// Entry name.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Inode number (or 0 if unknown).
    /// </summary>
    public ulong Ino { get; set; }

    /// <summary>
    /// File type (DT_REG, DT_DIR, etc.).
    /// </summary>
    public byte Type { get; set; }

    /// <summary>
    /// Optional stat structure.
    /// </summary>
    public FuseStat? Stat { get; set; }

    /// <summary>
    /// Directory entry type for regular file.
    /// </summary>
    public const byte DT_REG = 8;

    /// <summary>
    /// Directory entry type for directory.
    /// </summary>
    public const byte DT_DIR = 4;

    /// <summary>
    /// Directory entry type for symbolic link.
    /// </summary>
    public const byte DT_LNK = 10;

    /// <summary>
    /// Directory entry type for block device.
    /// </summary>
    public const byte DT_BLK = 6;

    /// <summary>
    /// Directory entry type for character device.
    /// </summary>
    public const byte DT_CHR = 2;

    /// <summary>
    /// Directory entry type for FIFO.
    /// </summary>
    public const byte DT_FIFO = 1;

    /// <summary>
    /// Directory entry type for socket.
    /// </summary>
    public const byte DT_SOCK = 12;

    /// <summary>
    /// Unknown directory entry type.
    /// </summary>
    public const byte DT_UNKNOWN = 0;
}

/// <summary>
/// FUSE file info structure.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FuseFileInfo
{
    /// <summary>
    /// Open flags.
    /// </summary>
    public int Flags;

    /// <summary>
    /// File handle.
    /// </summary>
    public ulong Fh;

    /// <summary>
    /// Lock owner ID.
    /// </summary>
    public ulong LockOwner;

    /// <summary>
    /// Poll handle (for poll operation).
    /// </summary>
    public nint PollHandle;

    /// <summary>
    /// Writeback cache enabled flag.
    /// </summary>
    public uint WritebackCache;

    /// <summary>
    /// Direct I/O flag.
    /// </summary>
    public uint DirectIo;

    /// <summary>
    /// Keep cache flag.
    /// </summary>
    public uint KeepCache;

    /// <summary>
    /// Flush flag.
    /// </summary>
    public uint Flush;

    /// <summary>
    /// Nonseekable flag.
    /// </summary>
    public uint Nonseekable;

    /// <summary>
    /// Flock release flag.
    /// </summary>
    public uint FlockRelease;

    /// <summary>
    /// Cache readdir flag.
    /// </summary>
    public uint CacheReaddir;
}

/// <summary>
/// FUSE timespec structure for nanosecond-precision timestamps.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FuseTimeSpec
{
    /// <summary>
    /// Seconds since epoch.
    /// </summary>
    public long Sec;

    /// <summary>
    /// Nanoseconds.
    /// </summary>
    public long Nsec;

    /// <summary>
    /// Special value to indicate "now".
    /// </summary>
    public const long UTIME_NOW = ((1L << 30) - 1L);

    /// <summary>
    /// Special value to indicate "omit this time".
    /// </summary>
    public const long UTIME_OMIT = ((1L << 30) - 2L);

    /// <summary>
    /// Creates a timespec from a DateTime.
    /// </summary>
    /// <param name="dt">The DateTime value.</param>
    /// <returns>A FuseTimeSpec.</returns>
    public static FuseTimeSpec FromDateTime(DateTime dt)
    {
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ts = dt.ToUniversalTime() - epoch;
        return new FuseTimeSpec
        {
            Sec = (long)ts.TotalSeconds,
            Nsec = (ts.Ticks % TimeSpan.TicksPerSecond) * 100
        };
    }

    /// <summary>
    /// Converts to a DateTime.
    /// </summary>
    /// <returns>A DateTime value.</returns>
    public readonly DateTime ToDateTime()
    {
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return epoch.AddSeconds(Sec).AddTicks(Nsec / 100);
    }
}

/// <summary>
/// FUSE statvfs structure for filesystem statistics.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FuseStatVfs
{
    /// <summary>
    /// Block size.
    /// </summary>
    public ulong Bsize;

    /// <summary>
    /// Fragment size.
    /// </summary>
    public ulong Frsize;

    /// <summary>
    /// Total blocks.
    /// </summary>
    public ulong Blocks;

    /// <summary>
    /// Free blocks.
    /// </summary>
    public ulong Bfree;

    /// <summary>
    /// Available blocks (non-privileged).
    /// </summary>
    public ulong Bavail;

    /// <summary>
    /// Total inodes.
    /// </summary>
    public ulong Files;

    /// <summary>
    /// Free inodes.
    /// </summary>
    public ulong Ffree;

    /// <summary>
    /// Available inodes (non-privileged).
    /// </summary>
    public ulong Favail;

    /// <summary>
    /// Filesystem ID.
    /// </summary>
    public ulong Fsid;

    /// <summary>
    /// Mount flags.
    /// </summary>
    public ulong Flag;

    /// <summary>
    /// Maximum filename length.
    /// </summary>
    public ulong Namemax;
}

/// <summary>
/// FUSE connection info structure.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FuseConnInfo
{
    /// <summary>
    /// Major protocol version.
    /// </summary>
    public uint ProtoMajor;

    /// <summary>
    /// Minor protocol version.
    /// </summary>
    public uint ProtoMinor;

    /// <summary>
    /// Maximum write size.
    /// </summary>
    public uint MaxWrite;

    /// <summary>
    /// Maximum read size.
    /// </summary>
    public uint MaxRead;

    /// <summary>
    /// Maximum readahead.
    /// </summary>
    public uint MaxReadahead;

    /// <summary>
    /// Capability flags (what the kernel supports).
    /// </summary>
    public uint Capable;

    /// <summary>
    /// Capability flags (what the filesystem wants).
    /// </summary>
    public uint Want;

    /// <summary>
    /// Maximum background requests.
    /// </summary>
    public uint MaxBackground;

    /// <summary>
    /// Congestion threshold.
    /// </summary>
    public uint CongestionThreshold;

    /// <summary>
    /// Time granularity in nanoseconds.
    /// </summary>
    public uint TimeGran;
}

/// <summary>
/// FUSE file lock structure.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FuseFlock
{
    /// <summary>
    /// Lock type (F_RDLCK, F_WRLCK, F_UNLCK).
    /// </summary>
    public short Type;

    /// <summary>
    /// Whence (SEEK_SET, SEEK_CUR, SEEK_END).
    /// </summary>
    public short Whence;

    /// <summary>
    /// Start offset.
    /// </summary>
    public long Start;

    /// <summary>
    /// Lock length (0 = to EOF).
    /// </summary>
    public long Len;

    /// <summary>
    /// Process ID holding lock.
    /// </summary>
    public int Pid;

    /// <summary>
    /// Read lock.
    /// </summary>
    public const short F_RDLCK = 0;

    /// <summary>
    /// Write lock.
    /// </summary>
    public const short F_WRLCK = 1;

    /// <summary>
    /// Unlock.
    /// </summary>
    public const short F_UNLCK = 2;
}

/// <summary>
/// File open flags.
/// </summary>
[Flags]
public enum OpenFlags
{
    /// <summary>
    /// Open for reading only.
    /// </summary>
    O_RDONLY = 0,

    /// <summary>
    /// Open for writing only.
    /// </summary>
    O_WRONLY = 1,

    /// <summary>
    /// Open for reading and writing.
    /// </summary>
    O_RDWR = 2,

    /// <summary>
    /// Access mode mask.
    /// </summary>
    O_ACCMODE = 3,

    /// <summary>
    /// Create file if it doesn't exist.
    /// </summary>
    O_CREAT = 0x40,

    /// <summary>
    /// Fail if file exists.
    /// </summary>
    O_EXCL = 0x80,

    /// <summary>
    /// Truncate file.
    /// </summary>
    O_TRUNC = 0x200,

    /// <summary>
    /// Append mode.
    /// </summary>
    O_APPEND = 0x400,

    /// <summary>
    /// Non-blocking mode.
    /// </summary>
    O_NONBLOCK = 0x800,

    /// <summary>
    /// Direct I/O mode.
    /// </summary>
    O_DIRECT = 0x4000,

    /// <summary>
    /// Directory only.
    /// </summary>
    O_DIRECTORY = 0x10000,

    /// <summary>
    /// Don't follow symlinks.
    /// </summary>
    O_NOFOLLOW = 0x20000
}

/// <summary>
/// Rename flags.
/// </summary>
[Flags]
public enum RenameFlags : uint
{
    /// <summary>
    /// No flags.
    /// </summary>
    None = 0,

    /// <summary>
    /// Don't replace existing file.
    /// </summary>
    RENAME_NOREPLACE = 1,

    /// <summary>
    /// Exchange source and destination.
    /// </summary>
    RENAME_EXCHANGE = 2,

    /// <summary>
    /// Whiteout source file (overlay filesystem).
    /// </summary>
    RENAME_WHITEOUT = 4
}
