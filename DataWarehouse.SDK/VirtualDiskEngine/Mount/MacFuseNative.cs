using DataWarehouse.SDK.Contracts;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mount;

/// <summary>
/// P/Invoke declarations for the macOS macFUSE libfuse3-compatible API.
/// Provides struct layouts matching macOS C headers, callback delegate types,
/// and native function imports for fuse_session lifecycle and reply operations.
/// </summary>
/// <remarks>
/// <para>
/// macFUSE 4.x ships two options: the traditional kext (<c>osxfuse</c>) and the newer
/// <c>fuse-t</c> (user-space, no kernel extension needed). Both expose a libfuse3-compatible API.
/// This P/Invoke layer detects either library at runtime.
/// </para>
/// <para>
/// Library loading priority:
/// <list type="number">
/// <item><c>libfuse-t.dylib</c> (macFUSE fuse-t, user-space, preferred)</item>
/// <item><c>/usr/local/lib/libfuse.dylib</c> (traditional macFUSE kext path)</item>
/// </list>
/// </para>
/// <para>
/// All structs use <c>[StructLayout(LayoutKind.Sequential)]</c> to match the C ABI
/// layout on macOS. The <see cref="Stat"/> struct uses macOS-specific field sizes and
/// layout (differs from Linux) including <c>st_birthtimespec</c>.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: macOS macFUSE P/Invoke bindings (VOPT-86)")]
[SupportedOSPlatform("osx")]
internal static class MacFuseNative
{
    private static readonly Lazy<IntPtr> LibraryHandle = new(LoadMacFuseLibrary);

    /// <summary>
    /// Returns true if a macFUSE-compatible library (libfuse-t or libfuse) can be loaded on macOS.
    /// </summary>
    public static bool IsAvailable() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && LibraryHandle.Value != IntPtr.Zero;

    private static IntPtr LoadMacFuseLibrary()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return IntPtr.Zero;

        // Try libfuse-t first (user-space, no kext needed, preferred for macOS 12+)
        if (NativeLibrary.TryLoad("libfuse-t.dylib", typeof(MacFuseNative).Assembly, null, out var handle))
            return handle;

        // Fall back to traditional macFUSE kext path
        if (NativeLibrary.TryLoad("/usr/local/lib/libfuse.dylib", typeof(MacFuseNative).Assembly, null, out handle))
            return handle;

        return IntPtr.Zero;
    }

    /// <summary>
    /// Gets a function pointer from the loaded macFUSE library.
    /// </summary>
    internal static IntPtr GetExport(string name)
    {
        var lib = LibraryHandle.Value;
        if (lib == IntPtr.Zero)
            throw new InvalidOperationException("macFUSE library is not loaded.");

        if (NativeLibrary.TryGetExport(lib, name, out var addr))
            return addr;

        throw new EntryPointNotFoundException($"Function '{name}' not found in macFUSE library.");
    }

    // ---------------------------------------------------------------
    // errno constants (macOS values -- some differ from Linux)
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

    /// <summary>Operation not supported (macOS).</summary>
    public const int Enotsup = 45;

    /// <summary>Directory not empty (macOS: 66, Linux: 39).</summary>
    public const int Enotempty = 66;

    /// <summary>Attribute not found (macOS-specific, replaces Linux ENODATA=61).</summary>
    public const int Enoattr = 93;

    // ---------------------------------------------------------------
    // Structs matching macOS C headers
    // ---------------------------------------------------------------

    /// <summary>
    /// POSIX timespec structure (16 bytes on both x86_64 and arm64 macOS).
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
    /// macOS stat structure layout. Differs from Linux in field sizes, order, and the
    /// presence of <c>st_birthtimespec</c> (creation time) and <c>st_flags</c>/<c>st_gen</c>.
    /// </summary>
    /// <remarks>
    /// <para>macOS stat is 144 bytes on both x86_64 and arm64.</para>
    /// <para>Key differences from Linux:</para>
    /// <list type="bullet">
    /// <item><c>st_dev</c> is int32 (Linux: uint64)</item>
    /// <item><c>st_mode</c> is uint16 (Linux: uint32)</item>
    /// <item><c>st_nlink</c> is uint16 (Linux: uint64)</item>
    /// <item>Includes <c>st_birthtimespec</c> (creation time)</item>
    /// <item>Includes <c>st_flags</c> and <c>st_gen</c> (BSD flags)</item>
    /// </list>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct Stat
    {
        /// <summary>ID of device containing file (int32 on macOS).</summary>
        public int st_dev;

        /// <summary>File type and permissions (uint16 on macOS).</summary>
        public ushort st_mode;

        /// <summary>Number of hard links (uint16 on macOS).</summary>
        public ushort st_nlink;

        /// <summary>Inode number (uint64 on macOS).</summary>
        public ulong st_ino;

        /// <summary>User ID of owner.</summary>
        public uint st_uid;

        /// <summary>Group ID of owner.</summary>
        public uint st_gid;

        /// <summary>Device ID if special file (int32 on macOS).</summary>
        public int st_rdev;

        /// <summary>Time of last access.</summary>
        public Timespec st_atimespec;

        /// <summary>Time of last modification.</summary>
        public Timespec st_mtimespec;

        /// <summary>Time of last status change.</summary>
        public Timespec st_ctimespec;

        /// <summary>Time of file creation (macOS-specific, birth time).</summary>
        public Timespec st_birthtimespec;

        /// <summary>Total size in bytes.</summary>
        public long st_size;

        /// <summary>Number of 512-byte blocks allocated.</summary>
        public long st_blocks;

        /// <summary>Optimal I/O block size.</summary>
        public int st_blksize;

        /// <summary>User-defined flags for file (BSD).</summary>
        public uint st_flags;

        /// <summary>File generation number (BSD).</summary>
        public uint st_gen;
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

        /// <summary>Inode attributes (macOS stat layout).</summary>
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
    /// Reuses the same layout as the Linux fuse_lowlevel_ops since macFUSE 4.x
    /// provides libfuse3 API compatibility.
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
    // (macOS uses the same libfuse3-compatible callback signatures)
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

    /// <summary>Callback: set an extended attribute (macOS has extra position parameter).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseSetxattrDelegate(IntPtr req, ulong ino, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr value, nuint size, int flags);

    /// <summary>Callback: get an extended attribute.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseGetxattrDelegate(IntPtr req, ulong ino, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint size);

    /// <summary>Callback: list extended attribute names.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseListxattrDelegate(IntPtr req, ulong ino, nuint size);

    // ---------------------------------------------------------------
    // P/Invoke via function pointer delegates (resolved from loaded library)
    // ---------------------------------------------------------------

    // Since macFUSE can be loaded from two different paths, we use NativeLibrary
    // and delegate-based invocation instead of [DllImport] with a fixed library name.

    /// <summary>Create a new FUSE session with low-level operations.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr FuseSessionNewDelegate(ref FuseArgs args, ref FuseLowlevelOps ops, nuint opSize, IntPtr userData);

    /// <summary>Mount a FUSE session at the specified directory.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int FuseSessionMountDelegate(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string mountpoint);

    /// <summary>Run the multi-threaded FUSE event loop.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int FuseSessionLoopMtDelegate(IntPtr session, ref FuseLoopConfig config);

    /// <summary>Unmount a FUSE session.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseSessionUnmountDelegate(IntPtr session);

    /// <summary>Destroy a FUSE session and free its resources.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseSessionDestroyDelegate(IntPtr session);

    /// <summary>Signal a FUSE session to exit the event loop.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseSessionExitDelegate(IntPtr session);

    /// <summary>Reply to a lookup/create request with entry information.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int FuseReplyEntryDelegate(IntPtr req, ref FuseEntryParam entry);

    /// <summary>Reply to a getattr/setattr request with file attributes.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int FuseReplyAttrDelegate(IntPtr req, ref Stat attr, double timeout);

    /// <summary>Reply to a readlink request with the symlink target.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int FuseReplyReadlinkDelegate(IntPtr req, [MarshalAs(UnmanagedType.LPUTF8Str)] string link);

    /// <summary>Reply to an open/opendir request with file info.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int FuseReplyOpenDelegate(IntPtr req, ref FuseFileInfo fi);

    /// <summary>Reply to a read/readdir request with a data buffer.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int FuseReplyBufDelegate(IntPtr req, IntPtr buf, nuint size);

    /// <summary>Reply to a write request with the number of bytes written.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int FuseReplyWriteDelegate(IntPtr req, nuint count);

    /// <summary>Reply to a request with an error code (0 = success).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int FuseReplyErrDelegate(IntPtr req, int err);

    /// <summary>Reply to a forget request (no response sent to caller).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseReplyNoneDelegate(IntPtr req);

    /// <summary>Reply to a statfs request with filesystem statistics.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int FuseReplyStatfsDelegate(IntPtr req, ref Statvfs stbuf);

    /// <summary>Reply to a getxattr/listxattr size query with the attribute size.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int FuseReplyXattrDelegate(IntPtr req, nuint count);

    /// <summary>Get the userdata pointer from a FUSE request.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr FuseReqUserdataDelegate(IntPtr req);

    /// <summary>Add a directory entry to a readdirplus buffer.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate nuint FuseAddDirentryPlusDelegate(IntPtr req, IntPtr buf, nuint bufsize,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref FuseEntryParam entry, long off);

    /// <summary>Reply to a create request with entry and file info.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int FuseReplyCreateDelegate(IntPtr req, ref FuseEntryParam entry, ref FuseFileInfo fi);

    /// <summary>Add an argument to fuse_args.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int FuseOptAddArgDelegate(ref FuseArgs args, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    /// <summary>Free fuse_args resources.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FuseOptFreeArgsDelegate(ref FuseArgs args);

    // ---------------------------------------------------------------
    // Cached delegate instances (resolved from loaded library)
    // ---------------------------------------------------------------

    private static T LoadFunction<T>(string name) where T : Delegate
    {
        var ptr = GetExport(name);
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    private static readonly Lazy<FuseSessionNewDelegate> FuseSessionNew = new(() => LoadFunction<FuseSessionNewDelegate>("fuse_session_new"));
    private static readonly Lazy<FuseSessionMountDelegate> FuseSessionMount = new(() => LoadFunction<FuseSessionMountDelegate>("fuse_session_mount"));
    private static readonly Lazy<FuseSessionLoopMtDelegate> FuseSessionLoopMt = new(() => LoadFunction<FuseSessionLoopMtDelegate>("fuse_session_loop_mt"));
    private static readonly Lazy<FuseSessionUnmountDelegate> FuseSessionUnmount = new(() => LoadFunction<FuseSessionUnmountDelegate>("fuse_session_unmount"));
    private static readonly Lazy<FuseSessionDestroyDelegate> FuseSessionDestroy = new(() => LoadFunction<FuseSessionDestroyDelegate>("fuse_session_destroy"));
    private static readonly Lazy<FuseSessionExitDelegate> FuseSessionExit = new(() => LoadFunction<FuseSessionExitDelegate>("fuse_session_exit"));
    private static readonly Lazy<FuseReplyEntryDelegate> FuseReplyEntry = new(() => LoadFunction<FuseReplyEntryDelegate>("fuse_reply_entry"));
    private static readonly Lazy<FuseReplyAttrDelegate> FuseReplyAttr = new(() => LoadFunction<FuseReplyAttrDelegate>("fuse_reply_attr"));
    private static readonly Lazy<FuseReplyReadlinkDelegate> FuseReplyReadlink = new(() => LoadFunction<FuseReplyReadlinkDelegate>("fuse_reply_readlink"));
    private static readonly Lazy<FuseReplyOpenDelegate> FuseReplyOpen = new(() => LoadFunction<FuseReplyOpenDelegate>("fuse_reply_open"));
    private static readonly Lazy<FuseReplyBufDelegate> FuseReplyBuf = new(() => LoadFunction<FuseReplyBufDelegate>("fuse_reply_buf"));
    private static readonly Lazy<FuseReplyWriteDelegate> FuseReplyWrite = new(() => LoadFunction<FuseReplyWriteDelegate>("fuse_reply_write"));
    private static readonly Lazy<FuseReplyErrDelegate> FuseReplyErr = new(() => LoadFunction<FuseReplyErrDelegate>("fuse_reply_err"));
    private static readonly Lazy<FuseReplyNoneDelegate> FuseReplyNone = new(() => LoadFunction<FuseReplyNoneDelegate>("fuse_reply_none"));
    private static readonly Lazy<FuseReplyStatfsDelegate> FuseReplyStatfs = new(() => LoadFunction<FuseReplyStatfsDelegate>("fuse_reply_statfs"));
    private static readonly Lazy<FuseReplyXattrDelegate> FuseReplyXattr = new(() => LoadFunction<FuseReplyXattrDelegate>("fuse_reply_xattr"));
    private static readonly Lazy<FuseReqUserdataDelegate> FuseReqUserdata = new(() => LoadFunction<FuseReqUserdataDelegate>("fuse_req_userdata"));
    private static readonly Lazy<FuseAddDirentryPlusDelegate> FuseAddDirentryPlus = new(() => LoadFunction<FuseAddDirentryPlusDelegate>("fuse_add_direntry_plus"));
    private static readonly Lazy<FuseReplyCreateDelegate> FuseReplyCreate = new(() => LoadFunction<FuseReplyCreateDelegate>("fuse_reply_create"));
    private static readonly Lazy<FuseOptAddArgDelegate> FuseOptAddArg = new(() => LoadFunction<FuseOptAddArgDelegate>("fuse_opt_add_arg"));
    private static readonly Lazy<FuseOptFreeArgsDelegate> FuseOptFreeArgs = new(() => LoadFunction<FuseOptFreeArgsDelegate>("fuse_opt_free_args"));

    // ---------------------------------------------------------------
    // Public API (same signatures as Fuse3Native for consistency)
    // ---------------------------------------------------------------

    /// <summary>Create a new FUSE session with low-level operations.</summary>
    public static IntPtr fuse_session_new(ref FuseArgs args, ref FuseLowlevelOps ops, nuint opSize, IntPtr userData)
        => FuseSessionNew.Value(ref args, ref ops, opSize, userData);

    /// <summary>Mount a FUSE session at the specified directory.</summary>
    public static int fuse_session_mount(IntPtr session, string mountpoint)
        => FuseSessionMount.Value(session, mountpoint);

    /// <summary>Run the multi-threaded FUSE event loop.</summary>
    public static int fuse_session_loop_mt(IntPtr session, ref FuseLoopConfig config)
        => FuseSessionLoopMt.Value(session, ref config);

    /// <summary>Unmount a FUSE session.</summary>
    public static void fuse_session_unmount(IntPtr session)
        => FuseSessionUnmount.Value(session);

    /// <summary>Destroy a FUSE session and free its resources.</summary>
    public static void fuse_session_destroy(IntPtr session)
        => FuseSessionDestroy.Value(session);

    /// <summary>Signal a FUSE session to exit the event loop.</summary>
    public static void fuse_session_exit(IntPtr session)
        => FuseSessionExit.Value(session);

    /// <summary>Reply to a lookup/create request with entry information.</summary>
    public static int fuse_reply_entry(IntPtr req, ref FuseEntryParam entry)
        => FuseReplyEntry.Value(req, ref entry);

    /// <summary>Reply to a getattr/setattr request with file attributes.</summary>
    public static int fuse_reply_attr(IntPtr req, ref Stat attr, double timeout)
        => FuseReplyAttr.Value(req, ref attr, timeout);

    /// <summary>Reply to a readlink request with the symlink target.</summary>
    public static int fuse_reply_readlink(IntPtr req, string link)
        => FuseReplyReadlink.Value(req, link);

    /// <summary>Reply to an open/opendir request with file info.</summary>
    public static int fuse_reply_open(IntPtr req, ref FuseFileInfo fi)
        => FuseReplyOpen.Value(req, ref fi);

    /// <summary>Reply to a read/readdir request with a data buffer.</summary>
    public static int fuse_reply_buf(IntPtr req, IntPtr buf, nuint size)
        => FuseReplyBuf.Value(req, buf, size);

    /// <summary>Reply to a write request with the number of bytes written.</summary>
    public static int fuse_reply_write(IntPtr req, nuint count)
        => FuseReplyWrite.Value(req, count);

    /// <summary>Reply to a request with an error code (0 = success).</summary>
    public static int fuse_reply_err(IntPtr req, int err)
        => FuseReplyErr.Value(req, err);

    /// <summary>Reply to a forget request (no response sent to caller).</summary>
    public static void fuse_reply_none(IntPtr req)
        => FuseReplyNone.Value(req);

    /// <summary>Reply to a statfs request with filesystem statistics.</summary>
    public static int fuse_reply_statfs(IntPtr req, ref Statvfs stbuf)
        => FuseReplyStatfs.Value(req, ref stbuf);

    /// <summary>Reply to a getxattr/listxattr size query with the attribute size.</summary>
    public static int fuse_reply_xattr(IntPtr req, nuint count)
        => FuseReplyXattr.Value(req, count);

    /// <summary>Get the userdata pointer from a FUSE request.</summary>
    public static IntPtr fuse_req_userdata(IntPtr req)
        => FuseReqUserdata.Value(req);

    /// <summary>Add a directory entry to a readdirplus buffer.</summary>
    public static nuint fuse_add_direntry_plus(IntPtr req, IntPtr buf, nuint bufsize,
        string name, ref FuseEntryParam entry, long off)
        => FuseAddDirentryPlus.Value(req, buf, bufsize, name, ref entry, off);

    /// <summary>Reply to a create request with entry and file info.</summary>
    public static int fuse_reply_create(IntPtr req, ref FuseEntryParam entry, ref FuseFileInfo fi)
        => FuseReplyCreate.Value(req, ref entry, ref fi);

    /// <summary>Add an argument to fuse_args.</summary>
    public static int fuse_opt_add_arg(ref FuseArgs args, string arg)
        => FuseOptAddArg.Value(ref args, arg);

    /// <summary>Free fuse_args resources.</summary>
    public static void fuse_opt_free_args(ref FuseArgs args)
        => FuseOptFreeArgs.Value(ref args);

    // ---------------------------------------------------------------
    // macOS mode constants (same values as Linux S_IF*)
    // ---------------------------------------------------------------

    /// <summary>Regular file mode bit.</summary>
    public const uint SIfreg = 0x8000;

    /// <summary>Directory mode bit.</summary>
    public const uint SIfdir = 0x4000;

    /// <summary>Symbolic link mode bit.</summary>
    public const uint SIflnk = 0xA000;
}
