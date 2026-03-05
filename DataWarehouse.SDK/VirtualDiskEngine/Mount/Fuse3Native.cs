using DataWarehouse.SDK.Contracts;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mount;

/// <summary>
/// P/Invoke declarations for the Linux libfuse3 low-level API.
/// Provides struct layouts matching C headers, callback delegate types,
/// and native function imports for fuse_session lifecycle and reply operations.
/// </summary>
/// <remarks>
/// <para>
/// This class targets the low-level inode-based FUSE API (fuse_lowlevel_ops) rather
/// than the high-level path-based API (fuse_operations). The low-level API provides
/// direct inode-level control matching VDE's native inode model, eliminating
/// path-to-inode translation overhead.
/// </para>
/// <para>
/// All structs use <c>[StructLayout(LayoutKind.Sequential)]</c> to match the C ABI
/// layout of libfuse3 on x86_64 Linux. Library availability is detected at runtime
/// via <see cref="NativeLibrary.TryLoad"/>.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Linux FUSE3 low-level P/Invoke bindings (VOPT-85)")]
[SupportedOSPlatform("linux")]
internal static class Fuse3Native
{
    private const string LibFuse3 = "libfuse3.so.3";
    private static readonly Lazy<bool> IsAvailableLazy = new(DetectLibrary);

    /// <summary>
    /// Returns true if libfuse3.so.3 can be loaded on this system.
    /// </summary>
    public static bool IsAvailable() => RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && IsAvailableLazy.Value;

    private static bool DetectLibrary()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        if (NativeLibrary.TryLoad(LibFuse3, typeof(Fuse3Native).Assembly, null, out var handle))
        {
            NativeLibrary.Free(handle);
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------
    // errno constants for fuse_reply_err
    // ---------------------------------------------------------------

    /// <summary>No such file or directory.</summary>
    public const int Enoent = 2;

    /// <summary>Input/output error.</summary>
    public const int Eio = 5;

    /// <summary>Permission denied.</summary>
    public const int Eacces = 13;

    /// <summary>File exists.</summary>
    public const int Eexist = 17;

    /// <summary>Not a directory.</summary>
    public const int Enotdir = 20;

    /// <summary>Is a directory.</summary>
    public const int Eisdir = 21;

    /// <summary>Invalid argument.</summary>
    public const int Einval = 22;

    /// <summary>No space left on device.</summary>
    public const int Enospc = 28;

    /// <summary>Read-only file system.</summary>
    public const int Erofs = 30;

    /// <summary>Function not implemented.</summary>
    public const int Enosys = 38;

    /// <summary>Directory not empty.</summary>
    public const int Enotempty = 39;

    /// <summary>No data available.</summary>
    public const int Enodata = 61;

    /// <summary>Operation not supported.</summary>
    public const int Enotsup = 95;

    // ---------------------------------------------------------------
    // Structs matching libfuse3 C headers (x86_64 Linux ABI)
    // ---------------------------------------------------------------

    /// <summary>
    /// POSIX timespec structure (16 bytes on x86_64).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Timespec
    {
        /// <summary>Seconds since epoch.</summary>
        public long tv_sec;

        /// <summary>Nanoseconds within the second.</summary>
        public long tv_nsec;
    }

    /// <summary>
    /// Linux stat structure (144 bytes on x86_64).
    /// Matches the kernel's struct stat layout for the x86_64 ABI.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Stat
    {
        /// <summary>ID of device containing file.</summary>
        public ulong st_dev;

        /// <summary>Inode number.</summary>
        public ulong st_ino;

        /// <summary>Number of hard links.</summary>
        public ulong st_nlink;

        /// <summary>File type and permissions.</summary>
        public uint st_mode;

        /// <summary>User ID of owner.</summary>
        public uint st_uid;

        /// <summary>Group ID of owner.</summary>
        public uint st_gid;

        /// <summary>Padding for alignment.</summary>
        private readonly uint _pad0;

        /// <summary>Device ID (if special file).</summary>
        public ulong st_rdev;

        /// <summary>Total size in bytes.</summary>
        public long st_size;

        /// <summary>Block size for filesystem I/O.</summary>
        public long st_blksize;

        /// <summary>Number of 512-byte blocks allocated.</summary>
        public long st_blocks;

        /// <summary>Time of last access.</summary>
        public Timespec st_atim;

        /// <summary>Time of last modification.</summary>
        public Timespec st_mtim;

        /// <summary>Time of last status change.</summary>
        public Timespec st_ctim;

        /// <summary>Reserved for future use.</summary>
        private readonly long _reserved0;

        /// <summary>Reserved for future use.</summary>
        private readonly long _reserved1;

        /// <summary>Reserved for future use.</summary>
        private readonly long _reserved2;
    }

    /// <summary>
    /// FUSE entry parameter returned by lookup and create callbacks.
    /// Contains inode number, generation, attributes, and cache timeouts.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FuseEntryParam
    {
        /// <summary>Inode number (must be non-zero, 1 = root).</summary>
        public ulong ino;

        /// <summary>Inode generation number (used for NFS export).</summary>
        public ulong generation;

        /// <summary>Inode attributes.</summary>
        public Stat attr;

        /// <summary>Attribute cache timeout in seconds.</summary>
        public double attr_timeout;

        /// <summary>Entry cache timeout in seconds.</summary>
        public double entry_timeout;
    }

    /// <summary>
    /// FUSE file information passed between open/read/write/release callbacks.
    /// The <see cref="fh"/> field is used to store the inode number for VDE lookups.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FuseFileInfo
    {
        /// <summary>Open flags (O_RDONLY, O_WRONLY, etc.).</summary>
        public int flags;

        /// <summary>Bitfield: direct_io(1), keep_cache(1), flush(1), nonseekable(1),
        /// flock_release(1), cache_readdir(1), padding(26).</summary>
        public uint bitfields;

        /// <summary>Old-style file handle (compatibility, unused).</summary>
        public uint fh_old;

        /// <summary>Lock owner ID.</summary>
        public ulong lock_owner;

        /// <summary>File handle -- VDE stores inode number here.</summary>
        public ulong fh;

        /// <summary>Sets the direct_io flag (bit 0).</summary>
        public void SetDirectIo(bool value) => bitfields = value ? (bitfields | 1u) : (bitfields & ~1u);

        /// <summary>Sets the keep_cache flag (bit 1).</summary>
        public void SetKeepCache(bool value) => bitfields = value ? (bitfields | 2u) : (bitfields & ~2u);
    }

    /// <summary>
    /// POSIX statvfs structure for filesystem statistics.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Statvfs
    {
        /// <summary>Filesystem block size.</summary>
        public ulong f_bsize;

        /// <summary>Fragment size.</summary>
        public ulong f_frsize;

        /// <summary>Total data blocks in filesystem.</summary>
        public ulong f_blocks;

        /// <summary>Free blocks in filesystem.</summary>
        public ulong f_bfree;

        /// <summary>Free blocks available to unprivileged user.</summary>
        public ulong f_bavail;

        /// <summary>Total file nodes in filesystem.</summary>
        public ulong f_files;

        /// <summary>Free file nodes in filesystem.</summary>
        public ulong f_ffree;

        /// <summary>Free file nodes available to unprivileged user.</summary>
        public ulong f_favail;

        /// <summary>Filesystem ID.</summary>
        public ulong f_fsid;

        /// <summary>Mount flags.</summary>
        public ulong f_flag;

        /// <summary>Maximum filename length.</summary>
        public ulong f_namemax;
    }

    /// <summary>
    /// FUSE arguments structure for session initialization.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FuseArgs
    {
        /// <summary>Number of arguments.</summary>
        public int argc;

        /// <summary>Argument array pointer.</summary>
        public IntPtr argv;

        /// <summary>Whether argv was allocated (1) or static (0).</summary>
        public int allocated;
    }

    /// <summary>
    /// Multi-threaded loop configuration for fuse_session_loop_mt.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FuseLoopConfig
    {
        /// <summary>Whether to clone file descriptors.</summary>
        public int clone_fd;

        /// <summary>Maximum number of idle threads.</summary>
        public uint max_idle_threads;
    }

    /// <summary>
    /// Low-level FUSE operations structure. Each field is a function pointer
    /// for a specific filesystem callback. Only populated fields are invoked.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FuseLowlevelOps
    {
        /// <summary>Initialize filesystem (called once at mount).</summary>
        public IntPtr init;

        /// <summary>Clean up filesystem (called at unmount).</summary>
        public IntPtr destroy;

        /// <summary>Look up a directory entry by name and get its attributes.</summary>
        public IntPtr lookup;

        /// <summary>Forget about an inode (decrement lookup count).</summary>
        public IntPtr forget;

        /// <summary>Get file attributes.</summary>
        public IntPtr getattr;

        /// <summary>Set file attributes.</summary>
        public IntPtr setattr;

        /// <summary>Read symbolic link target.</summary>
        public IntPtr readlink;

        /// <summary>Create file node (regular file, device, socket, FIFO).</summary>
        public IntPtr mknod;

        /// <summary>Create a directory.</summary>
        public IntPtr mkdir;

        /// <summary>Remove a file.</summary>
        public IntPtr unlink;

        /// <summary>Remove a directory.</summary>
        public IntPtr rmdir;

        /// <summary>Create a symbolic link.</summary>
        public IntPtr symlink;

        /// <summary>Rename a file/directory.</summary>
        public IntPtr rename;

        /// <summary>Create a hard link.</summary>
        public IntPtr link;

        /// <summary>Open a file.</summary>
        public IntPtr open;

        /// <summary>Read data from an open file.</summary>
        public IntPtr read;

        /// <summary>Write data to an open file.</summary>
        public IntPtr write;

        /// <summary>Flush cached data on close.</summary>
        public IntPtr flush;

        /// <summary>Release an open file (called when all file descriptors closed).</summary>
        public IntPtr release;

        /// <summary>Synchronize file contents.</summary>
        public IntPtr fsync;

        /// <summary>Open a directory.</summary>
        public IntPtr opendir;

        /// <summary>Read directory entries.</summary>
        public IntPtr readdir;

        /// <summary>Release an open directory.</summary>
        public IntPtr releasedir;

        /// <summary>Synchronize directory contents.</summary>
        public IntPtr fsyncdir;

        /// <summary>Get filesystem statistics.</summary>
        public IntPtr statfs;

        /// <summary>Set an extended attribute.</summary>
        public IntPtr setxattr;

        /// <summary>Get an extended attribute.</summary>
        public IntPtr getxattr;

        /// <summary>List extended attribute names.</summary>
        public IntPtr listxattr;

        /// <summary>Remove an extended attribute.</summary>
        public IntPtr removexattr;

        /// <summary>Check file access permissions (not used by low-level API).</summary>
        public IntPtr access;

        /// <summary>Create and open a file atomically.</summary>
        public IntPtr create;
    }

    // ---------------------------------------------------------------
    // Callback delegates matching fuse_lowlevel_ops signatures
    // ---------------------------------------------------------------

    /// <summary>Callback: look up directory entry by name.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseLookupDelegate(IntPtr req, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    /// <summary>Callback: forget an inode (decrement nlookup).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseForgetDelegate(IntPtr req, ulong ino, ulong nlookup);

    /// <summary>Callback: get file attributes.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseGetattrDelegate(IntPtr req, ulong ino, IntPtr fi);

    /// <summary>Callback: set file attributes.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseSetattrDelegate(IntPtr req, ulong ino, ref Stat attr, int toSet, IntPtr fi);

    /// <summary>Callback: read symbolic link target.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseReadlinkDelegate(IntPtr req, ulong ino);

    /// <summary>Callback: create a directory.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseMkdirDelegate(IntPtr req, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint mode);

    /// <summary>Callback: remove a file.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseUnlinkDelegate(IntPtr req, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    /// <summary>Callback: remove a directory.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseRmdirDelegate(IntPtr req, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    /// <summary>Callback: create a symbolic link.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseSymlinkDelegate(IntPtr req, [MarshalAs(UnmanagedType.LPUTF8Str)] string link, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    /// <summary>Callback: rename a file or directory.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseRenameDelegate(IntPtr req, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, ulong newparent, [MarshalAs(UnmanagedType.LPUTF8Str)] string newname, uint flags);

    /// <summary>Callback: open a file.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseOpenDelegate(IntPtr req, ulong ino, IntPtr fi);

    /// <summary>Callback: read data from an open file.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseReadDelegate(IntPtr req, ulong ino, nuint size, long off, IntPtr fi);

    /// <summary>Callback: write data to an open file.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseWriteDelegate(IntPtr req, ulong ino, IntPtr buf, nuint size, long off, IntPtr fi);

    /// <summary>Callback: flush cached data on file close.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseFlushDelegate(IntPtr req, ulong ino, IntPtr fi);

    /// <summary>Callback: release an open file handle.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseReleaseDelegate(IntPtr req, ulong ino, IntPtr fi);

    /// <summary>Callback: open a directory.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseOpendirDelegate(IntPtr req, ulong ino, IntPtr fi);

    /// <summary>Callback: read directory entries (readdirplus).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseReaddirDelegate(IntPtr req, ulong ino, nuint size, long off, IntPtr fi);

    /// <summary>Callback: release an open directory handle.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseReleasedirDelegate(IntPtr req, ulong ino, IntPtr fi);

    /// <summary>Callback: get filesystem statistics.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseStatfsDelegate(IntPtr req, ulong ino);

    /// <summary>Callback: create and open a file atomically.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseCreateDelegate(IntPtr req, ulong parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint mode, IntPtr fi);

    /// <summary>Callback: set an extended attribute.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseSetxattrDelegate(IntPtr req, ulong ino, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr value, nuint size, int flags);

    /// <summary>Callback: get an extended attribute.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseGetxattrDelegate(IntPtr req, ulong ino, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint size);

    /// <summary>Callback: list extended attribute names.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseListxattrDelegate(IntPtr req, ulong ino, nuint size);

    // ---------------------------------------------------------------
    // P/Invoke declarations (libfuse3.so.3)
    // ---------------------------------------------------------------

    /// <summary>
    /// Create a new FUSE session with low-level operations.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr fuse_session_new(ref FuseArgs args, ref FuseLowlevelOps ops, nuint opSize, IntPtr userData);

    /// <summary>
    /// Mount a FUSE session at the specified directory.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fuse_session_mount(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string mountpoint);

    /// <summary>
    /// Run the single-threaded FUSE event loop.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fuse_session_loop(IntPtr session);

    /// <summary>
    /// Run the multi-threaded FUSE event loop with the specified configuration.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fuse_session_loop_mt(IntPtr session, ref FuseLoopConfig config);

    /// <summary>
    /// Unmount a FUSE session.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fuse_session_unmount(IntPtr session);

    /// <summary>
    /// Destroy a FUSE session and free its resources.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fuse_session_destroy(IntPtr session);

    /// <summary>
    /// Signal a FUSE session to exit the event loop.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fuse_session_exit(IntPtr session);

    // ---------------------------------------------------------------
    // Reply functions
    // ---------------------------------------------------------------

    /// <summary>
    /// Reply to a lookup/create request with entry information.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fuse_reply_entry(IntPtr req, ref FuseEntryParam entry);

    /// <summary>
    /// Reply to a getattr/setattr request with file attributes.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fuse_reply_attr(IntPtr req, ref Stat attr, double timeout);

    /// <summary>
    /// Reply to a readlink request with the symlink target.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fuse_reply_readlink(IntPtr req, [MarshalAs(UnmanagedType.LPUTF8Str)] string link);

    /// <summary>
    /// Reply to an open/opendir request with file info.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fuse_reply_open(IntPtr req, ref FuseFileInfo fi);

    /// <summary>
    /// Reply to a read/readdir request with a data buffer.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fuse_reply_buf(IntPtr req, IntPtr buf, nuint size);

    /// <summary>
    /// Reply to a write request with the number of bytes written.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fuse_reply_write(IntPtr req, nuint count);

    /// <summary>
    /// Reply to a request with an error code (0 = success).
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fuse_reply_err(IntPtr req, int err);

    /// <summary>
    /// Reply to a forget request (no response sent to caller).
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fuse_reply_none(IntPtr req);

    /// <summary>
    /// Reply to a statfs request with filesystem statistics.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fuse_reply_statfs(IntPtr req, ref Statvfs stbuf);

    /// <summary>
    /// Reply to a getxattr/listxattr size query with the attribute size.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fuse_reply_xattr(IntPtr req, nuint count);

    /// <summary>
    /// Get the userdata pointer from a FUSE request.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr fuse_req_userdata(IntPtr req);

    /// <summary>
    /// Add a directory entry to a readdirplus buffer.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint fuse_add_direntry_plus(IntPtr req, IntPtr buf, nuint bufsize,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref FuseEntryParam entry, long off);

    /// <summary>
    /// Reply to a create request with entry and file info.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fuse_reply_create(IntPtr req, ref FuseEntryParam entry, ref FuseFileInfo fi);

    // ---------------------------------------------------------------
    // Argument helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Add an argument to fuse_args.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern int fuse_opt_add_arg(ref FuseArgs args, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    /// <summary>
    /// Free fuse_args resources.
    /// </summary>
    [DllImport(LibFuse3, CallingConvention = CallingConvention.Cdecl)]
    public static extern void fuse_opt_free_args(ref FuseArgs args);

    // ---------------------------------------------------------------
    // Linux mode constants (S_IF*)
    // ---------------------------------------------------------------

    /// <summary>Regular file mode bit.</summary>
    public const uint SIfreg = 0x8000;

    /// <summary>Directory mode bit.</summary>
    public const uint SIfdir = 0x4000;

    /// <summary>Symbolic link mode bit.</summary>
    public const uint SIflnk = 0xA000;
}
