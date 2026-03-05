using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Cache;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;
using DataWarehouse.SDK.VirtualDiskEngine.Metadata;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mount;

/// <summary>
/// Linux FUSE3 low-level mount provider implementing <see cref="IVdeMountProvider"/>.
/// Mounts VDE volumes as native Linux directories via P/Invoke to libfuse3's
/// low-level inode-based API (fuse_lowlevel_ops).
/// </summary>
/// <remarks>
/// <para>
/// Uses the low-level FUSE API (not the high-level path-based API) for zero
/// path-to-inode translation overhead. VDE's native inode model maps directly
/// to FUSE inode numbers, and all filesystem callbacks delegate to
/// <see cref="VdeFilesystemAdapter"/> for storage logic.
/// </para>
/// <para>
/// The multi-threaded event loop (<c>fuse_session_loop_mt</c>) provides concurrent
/// request handling. Linux kernel 6.9+ FUSE passthrough mode enables near-native
/// performance for unencrypted data.
/// </para>
/// <para>
/// This provider requires libfuse3.so.3 to be installed on the system.
/// Use <see cref="IsAvailable"/> to check before mounting.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Linux FUSE3 low-level mount provider (VOPT-85)")]
[SupportedOSPlatform("linux")]
public sealed class Fuse3LowLevelMountProvider : IVdeMountProvider
{
    private readonly IBlockDevice _device;
    private readonly IInodeTable _inodeTable;
    private readonly IBlockAllocator _allocator;
    private readonly IWriteAheadLog _wal;
    private readonly IArcCache _cache;
    private readonly ConcurrentDictionary<string, Fuse3MountHandle> _activeMounts = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a new FUSE3 low-level mount provider.
    /// </summary>
    /// <param name="device">Block device for raw I/O.</param>
    /// <param name="inodeTable">Inode table for metadata operations.</param>
    /// <param name="allocator">Block allocator for space management.</param>
    /// <param name="wal">Write-ahead log for crash consistency.</param>
    /// <param name="cache">ARC cache for hot-block acceleration.</param>
    public Fuse3LowLevelMountProvider(
        IBlockDevice device,
        IInodeTable inodeTable,
        IBlockAllocator allocator,
        IWriteAheadLog wal,
        IArcCache cache)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _inodeTable = inodeTable ?? throw new ArgumentNullException(nameof(inodeTable));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _wal = wal ?? throw new ArgumentNullException(nameof(wal));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <inheritdoc />
    public PlatformFlags SupportedPlatforms => PlatformFlags.Linux;

    /// <inheritdoc />
    public bool IsAvailable => Fuse3Native.IsAvailable() && RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <inheritdoc />
    public async Task<IMountHandle> MountAsync(string vdePath, string mountPoint, MountOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(vdePath))
            throw new ArgumentException("VDE path must not be empty.", nameof(vdePath));
        if (string.IsNullOrEmpty(mountPoint))
            throw new ArgumentException("Mount point must not be empty.", nameof(mountPoint));
        options ??= new MountOptions();

        if (!IsAvailable)
            throw new InvalidOperationException("FUSE3 is not available on this platform. Ensure libfuse3.so.3 is installed.");
        if (!File.Exists(vdePath))
            throw new FileNotFoundException("VDE volume file not found.", vdePath);
        if (!Directory.Exists(mountPoint))
            throw new DirectoryNotFoundException($"Mount point directory does not exist: {mountPoint}");

        if (_activeMounts.ContainsKey(mountPoint))
            throw new InvalidOperationException($"A VDE volume is already mounted at '{mountPoint}'.");

        // Create the shared filesystem adapter
        var adapter = new VdeFilesystemAdapter(_device, _inodeTable, _allocator, _wal, _cache, options);

        // Allocate GCHandle for the adapter to pass as userdata to fuse_session_new
        var userDataHandle = GCHandle.Alloc(adapter);
        var delegateHandles = new List<GCHandle>();

        try
        {
            // Build fuse_args from MountOptions
            var args = new Fuse3Native.FuseArgs();
            Fuse3Native.fuse_opt_add_arg(ref args, "vde-fuse3");

            AddFuseOption(ref args, "default_permissions");
            if (options.AllowOther)
                AddFuseOption(ref args, "allow_other");
            if (options.AutoUnmount)
                AddFuseOption(ref args, "auto_unmount");
            if (options.EnableWriteback)
                AddFuseOption(ref args, "writeback_cache");
            if (options.ReadOnly)
                AddFuseOption(ref args, "ro");

            AddFuseOption(ref args, $"attr_timeout={options.AttrTimeout.TotalSeconds:F1}");
            AddFuseOption(ref args, $"entry_timeout={options.EntryTimeout.TotalSeconds:F1}");
            AddFuseOption(ref args, $"max_idle_threads={options.MaxConcurrentOps}");

            foreach (var kvp in options.PlatformOptions)
            {
                AddFuseOption(ref args, $"{kvp.Key}={kvp.Value}");
            }

            // Build the low-level ops struct with callback delegates
            var ops = new Fuse3Native.FuseLowlevelOps();
            PopulateOps(ref ops, delegateHandles);

            var opsSize = (nuint)Marshal.SizeOf<Fuse3Native.FuseLowlevelOps>();
            var session = Fuse3Native.fuse_session_new(ref args, ref ops, opsSize, GCHandle.ToIntPtr(userDataHandle));

            Fuse3Native.fuse_opt_free_args(ref args);

            if (session == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create FUSE session. Check that libfuse3 is properly installed.");

            var mountResult = Fuse3Native.fuse_session_mount(session, mountPoint);
            if (mountResult != 0)
            {
                Fuse3Native.fuse_session_destroy(session);
                throw new InvalidOperationException($"Failed to mount FUSE session at '{mountPoint}'. Error code: {mountResult}");
            }

            // Start the multi-threaded event loop on a background thread
            var loopConfig = new Fuse3Native.FuseLoopConfig
            {
                clone_fd = 1,
                max_idle_threads = (uint)Math.Max(1, options.MaxConcurrentOps)
            };

            var loopThread = new Thread(() =>
            {
                try
                {
                    var cfg = loopConfig;
                    Fuse3Native.fuse_session_loop_mt(session, ref cfg);
                }
                catch
                {
                    // Loop exited -- normal during unmount
                }
            })
            {
                IsBackground = true,
                Name = $"VDE-FUSE3-{Path.GetFileName(mountPoint)}"
            };

            loopThread.Start();

            var handle = new Fuse3MountHandle(
                mountPoint,
                vdePath,
                session,
                adapter,
                loopThread,
                userDataHandle,
                delegateHandles.ToArray(),
                options);

            if (!_activeMounts.TryAdd(mountPoint, handle))
            {
                await handle.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException($"Race condition: mount point '{mountPoint}' was claimed concurrently.");
            }

            return handle;
        }
        catch
        {
            if (userDataHandle.IsAllocated)
                userDataHandle.Free();
            foreach (var h in delegateHandles)
            {
                if (h.IsAllocated) h.Free();
            }
            await adapter.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task UnmountAsync(IMountHandle handle, CancellationToken ct = default)
    {
        if (handle is not Fuse3MountHandle fuseHandle)
            throw new ArgumentException("Handle was not created by this provider.", nameof(handle));

        _activeMounts.TryRemove(fuseHandle.MountPoint, out _);
        await fuseHandle.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MountInfo>> ListMountsAsync(CancellationToken ct = default)
    {
        var list = _activeMounts.Values
            .Select(h => h.ToMountInfo())
            .ToList();

        return Task.FromResult<IReadOnlyList<MountInfo>>(list);
    }

    // ---------------------------------------------------------------
    // Internal: Build fuse_lowlevel_ops with callback delegates
    // ---------------------------------------------------------------

    private static void AddFuseOption(ref Fuse3Native.FuseArgs args, string option)
    {
        Fuse3Native.fuse_opt_add_arg(ref args, $"-o{option}");
    }

    private static void PopulateOps(ref Fuse3Native.FuseLowlevelOps ops, List<GCHandle> handles)
    {
        ops.lookup = PinDelegate<Fuse3Native.FuseLookupDelegate>(OnLookup, handles);
        ops.forget = PinDelegate<Fuse3Native.FuseForgetDelegate>(OnForget, handles);
        ops.getattr = PinDelegate<Fuse3Native.FuseGetattrDelegate>(OnGetattr, handles);
        ops.setattr = PinDelegate<Fuse3Native.FuseSetattrDelegate>(OnSetattr, handles);
        ops.readlink = PinDelegate<Fuse3Native.FuseReadlinkDelegate>(OnReadlink, handles);
        ops.mkdir = PinDelegate<Fuse3Native.FuseMkdirDelegate>(OnMkdir, handles);
        ops.unlink = PinDelegate<Fuse3Native.FuseUnlinkDelegate>(OnUnlink, handles);
        ops.rmdir = PinDelegate<Fuse3Native.FuseRmdirDelegate>(OnRmdir, handles);
        ops.symlink = PinDelegate<Fuse3Native.FuseSymlinkDelegate>(OnSymlink, handles);
        ops.rename = PinDelegate<Fuse3Native.FuseRenameDelegate>(OnRename, handles);
        ops.open = PinDelegate<Fuse3Native.FuseOpenDelegate>(OnOpen, handles);
        ops.read = PinDelegate<Fuse3Native.FuseReadDelegate>(OnRead, handles);
        ops.write = PinDelegate<Fuse3Native.FuseWriteDelegate>(OnWrite, handles);
        ops.flush = PinDelegate<Fuse3Native.FuseFlushDelegate>(OnFlush, handles);
        ops.release = PinDelegate<Fuse3Native.FuseReleaseDelegate>(OnRelease, handles);
        ops.opendir = PinDelegate<Fuse3Native.FuseOpendirDelegate>(OnOpendir, handles);
        ops.readdir = PinDelegate<Fuse3Native.FuseReaddirDelegate>(OnReaddir, handles);
        ops.releasedir = PinDelegate<Fuse3Native.FuseReleasedirDelegate>(OnReleasedir, handles);
        ops.statfs = PinDelegate<Fuse3Native.FuseStatfsDelegate>(OnStatfs, handles);
        ops.create = PinDelegate<Fuse3Native.FuseCreateDelegate>(OnCreate, handles);
        ops.setxattr = PinDelegate<Fuse3Native.FuseSetxattrDelegate>(OnSetxattr, handles);
        ops.getxattr = PinDelegate<Fuse3Native.FuseGetxattrDelegate>(OnGetxattr, handles);
        ops.listxattr = PinDelegate<Fuse3Native.FuseListxattrDelegate>(OnListxattr, handles);
    }

    private static IntPtr PinDelegate<T>(T callback, List<GCHandle> handles) where T : Delegate
    {
        var handle = GCHandle.Alloc(callback);
        handles.Add(handle);
        return Marshal.GetFunctionPointerForDelegate(callback);
    }

    // ---------------------------------------------------------------
    // Helper: retrieve adapter from FUSE request userdata
    // ---------------------------------------------------------------

    private static VdeFilesystemAdapter GetAdapter(IntPtr req)
    {
        var ptr = Fuse3Native.fuse_req_userdata(req);
        var gcHandle = GCHandle.FromIntPtr(ptr);
        return (VdeFilesystemAdapter)gcHandle.Target!;
    }

    // ---------------------------------------------------------------
    // InodeAttributes -> Stat conversion
    // ---------------------------------------------------------------

    private static Fuse3Native.Stat ToStat(InodeAttributes attrs)
    {
        var st = new Fuse3Native.Stat
        {
            st_ino = (ulong)attrs.InodeNumber,
            st_mode = InodeTypeToMode(attrs.Type) | PermissionsToMode(attrs.Permissions),
            st_nlink = (ulong)Math.Max(1, attrs.LinkCount),
            st_uid = (uint)(attrs.OwnerId & 0xFFFFFFFF),
            st_gid = 0,
            st_size = attrs.Size,
            st_blksize = attrs.BlockSize > 0 ? attrs.BlockSize : 4096,
            st_blocks = attrs.AllocatedSize / 512
        };

        st.st_atim = UnixSecondsToTimespec(attrs.AccessedUtc);
        st.st_mtim = UnixSecondsToTimespec(attrs.ModifiedUtc);
        st.st_ctim = UnixSecondsToTimespec(attrs.ChangedUtc);

        return st;
    }

    private static uint InodeTypeToMode(InodeType type)
    {
        return type switch
        {
            InodeType.File => Fuse3Native.S_IFREG,
            InodeType.Directory => Fuse3Native.S_IFDIR,
            InodeType.SymLink => Fuse3Native.S_IFLNK,
            _ => Fuse3Native.S_IFREG
        };
    }

    private static uint PermissionsToMode(InodePermissions perms)
    {
        return (uint)perms & 0x1FF; // Lower 9 bits = rwxrwxrwx
    }

    private static Fuse3Native.Timespec UnixSecondsToTimespec(long unixSeconds)
    {
        return new Fuse3Native.Timespec
        {
            tv_sec = unixSeconds,
            tv_nsec = 0
        };
    }

    // ---------------------------------------------------------------
    // Error mapping: .NET exception -> errno
    // ---------------------------------------------------------------

    private static int ExceptionToErrno(Exception ex)
    {
        return ex switch
        {
            FileNotFoundException => Fuse3Native.ENOENT,
            DirectoryNotFoundException => Fuse3Native.ENOENT,
            UnauthorizedAccessException => Fuse3Native.EACCES,
            InvalidOperationException e when e.Message.Contains("read-only", StringComparison.OrdinalIgnoreCase) => Fuse3Native.EROFS,
            InvalidOperationException e when e.Message.Contains("not empty", StringComparison.OrdinalIgnoreCase) => Fuse3Native.ENOTEMPTY,
            IOException e when e.Message.Contains("disk full", StringComparison.OrdinalIgnoreCase)
                            || e.Message.Contains("no space", StringComparison.OrdinalIgnoreCase) => Fuse3Native.ENOSPC,
            IOException e when e.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) => Fuse3Native.EEXIST,
            ArgumentException => Fuse3Native.EINVAL,
            NotSupportedException => Fuse3Native.ENOTSUP,
            _ => Fuse3Native.EIO
        };
    }

    // ---------------------------------------------------------------
    // FUSE low-level callbacks
    // All callbacks are synchronous from libfuse's perspective.
    // The multi-threaded loop (fuse_session_loop_mt) provides concurrency.
    // Async adapter methods are awaited synchronously via GetAwaiter().GetResult().
    // ---------------------------------------------------------------

    private static void OnLookup(IntPtr req, ulong parent, string name)
    {
        try
        {
            var adapter = GetAdapter(req);
            var result = adapter.LookupAsync((long)parent, name, CancellationToken.None).GetAwaiter().GetResult();

            if (!result.Found)
            {
                Fuse3Native.fuse_reply_err(req, Fuse3Native.ENOENT);
                return;
            }

            var attrs = adapter.GetattrAsync(result.InodeNumber, CancellationToken.None).GetAwaiter().GetResult();
            var entry = new Fuse3Native.FuseEntryParam
            {
                ino = (ulong)result.InodeNumber,
                generation = 1,
                attr = ToStat(attrs),
                attr_timeout = 1.0,
                entry_timeout = 1.0
            };

            Fuse3Native.fuse_reply_entry(req, ref entry);
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnForget(IntPtr req, ulong ino, ulong nlookup)
    {
        // VDE manages inode lifecycle via refcounting in IInodeTable.
        // No action needed on forget.
        Fuse3Native.fuse_reply_none(req);
    }

    private static void OnGetattr(IntPtr req, ulong ino, IntPtr fi)
    {
        try
        {
            var adapter = GetAdapter(req);
            var attrs = adapter.GetattrAsync((long)ino, CancellationToken.None).GetAwaiter().GetResult();
            var st = ToStat(attrs);
            Fuse3Native.fuse_reply_attr(req, ref st, 1.0);
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnSetattr(IntPtr req, ulong ino, ref Fuse3Native.Stat attr, int toSet, IntPtr fi)
    {
        try
        {
            var adapter = GetAdapter(req);

            // Build InodeAttributes and mask from the FUSE toSet bitfield
            var mask = InodeAttributeMask.None;
            var inodeAttrs = new InodeAttributes { InodeNumber = (long)ino };

            // FUSE_SET_ATTR_MODE = (1 << 0)
            if ((toSet & (1 << 0)) != 0)
            {
                mask |= InodeAttributeMask.Permissions;
                inodeAttrs = inodeAttrs with { Permissions = (InodePermissions)(attr.st_mode & 0x1FF) };
            }

            // FUSE_SET_ATTR_UID = (1 << 1)
            if ((toSet & (1 << 1)) != 0)
            {
                mask |= InodeAttributeMask.Owner;
                inodeAttrs = inodeAttrs with { OwnerId = attr.st_uid };
            }

            // FUSE_SET_ATTR_SIZE = (1 << 3)
            if ((toSet & (1 << 3)) != 0)
            {
                mask |= InodeAttributeMask.Size;
                inodeAttrs = inodeAttrs with { Size = attr.st_size };
            }

            // FUSE_SET_ATTR_ATIME = (1 << 4)
            if ((toSet & (1 << 4)) != 0)
            {
                mask |= InodeAttributeMask.AccessTime;
                inodeAttrs = inodeAttrs with { AccessedUtc = attr.st_atim.tv_sec };
            }

            // FUSE_SET_ATTR_MTIME = (1 << 5)
            if ((toSet & (1 << 5)) != 0)
            {
                mask |= InodeAttributeMask.ModifyTime;
                inodeAttrs = inodeAttrs with { ModifiedUtc = attr.st_mtim.tv_sec };
            }

            adapter.SetattrAsync((long)ino, inodeAttrs, mask, CancellationToken.None).GetAwaiter().GetResult();

            // Reply with updated attributes
            var updatedAttrs = adapter.GetattrAsync((long)ino, CancellationToken.None).GetAwaiter().GetResult();
            var st = ToStat(updatedAttrs);
            Fuse3Native.fuse_reply_attr(req, ref st, 1.0);
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnReadlink(IntPtr req, ulong ino)
    {
        try
        {
            var adapter = GetAdapter(req);
            var target = adapter.ReadlinkAsync((long)ino, CancellationToken.None).GetAwaiter().GetResult();
            Fuse3Native.fuse_reply_readlink(req, target);
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnMkdir(IntPtr req, ulong parent, string name, uint mode)
    {
        try
        {
            var adapter = GetAdapter(req);
            var permissions = (InodePermissions)(mode & 0x1FF);
            var ino = adapter.MkdirAsync((long)parent, name, permissions, CancellationToken.None).GetAwaiter().GetResult();

            var attrs = adapter.GetattrAsync(ino, CancellationToken.None).GetAwaiter().GetResult();
            var entry = new Fuse3Native.FuseEntryParam
            {
                ino = (ulong)ino,
                generation = 1,
                attr = ToStat(attrs),
                attr_timeout = 1.0,
                entry_timeout = 1.0
            };

            Fuse3Native.fuse_reply_entry(req, ref entry);
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnUnlink(IntPtr req, ulong parent, string name)
    {
        try
        {
            var adapter = GetAdapter(req);
            adapter.UnlinkAsync((long)parent, name, CancellationToken.None).GetAwaiter().GetResult();
            Fuse3Native.fuse_reply_err(req, 0);
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnRmdir(IntPtr req, ulong parent, string name)
    {
        try
        {
            var adapter = GetAdapter(req);
            adapter.RmdirAsync((long)parent, name, CancellationToken.None).GetAwaiter().GetResult();
            Fuse3Native.fuse_reply_err(req, 0);
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnSymlink(IntPtr req, string link, ulong parent, string name)
    {
        try
        {
            var adapter = GetAdapter(req);
            var ino = adapter.SymlinkAsync((long)parent, name, link, CancellationToken.None).GetAwaiter().GetResult();

            var attrs = adapter.GetattrAsync(ino, CancellationToken.None).GetAwaiter().GetResult();
            var entry = new Fuse3Native.FuseEntryParam
            {
                ino = (ulong)ino,
                generation = 1,
                attr = ToStat(attrs),
                attr_timeout = 1.0,
                entry_timeout = 1.0
            };

            Fuse3Native.fuse_reply_entry(req, ref entry);
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnRename(IntPtr req, ulong parent, string name, ulong newparent, string newname, uint flags)
    {
        try
        {
            var adapter = GetAdapter(req);
            adapter.RenameAsync((long)parent, name, (long)newparent, newname, CancellationToken.None).GetAwaiter().GetResult();
            Fuse3Native.fuse_reply_err(req, 0);
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnOpen(IntPtr req, ulong ino, IntPtr fi)
    {
        try
        {
            var adapter = GetAdapter(req);

            // Validate the inode exists
            adapter.GetattrAsync((long)ino, CancellationToken.None).GetAwaiter().GetResult();

            // Store inode number in fi.fh for subsequent read/write
            var fileInfo = Marshal.PtrToStructure<Fuse3Native.FuseFileInfo>(fi);
            fileInfo.fh = ino;
            Marshal.StructureToPtr(fileInfo, fi, false);

            Fuse3Native.fuse_reply_open(req, ref fileInfo);
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnRead(IntPtr req, ulong ino, nuint size, long off, IntPtr fi)
    {
        try
        {
            var adapter = GetAdapter(req);
            var buffer = new byte[(int)size];
            var bytesRead = adapter.ReadAsync((long)ino, buffer.AsMemory(), off, CancellationToken.None).GetAwaiter().GetResult();

            unsafe
            {
                fixed (byte* bufPtr = buffer)
                {
                    Fuse3Native.fuse_reply_buf(req, (IntPtr)bufPtr, (nuint)bytesRead);
                }
            }
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnWrite(IntPtr req, ulong ino, IntPtr buf, nuint size, long off, IntPtr fi)
    {
        try
        {
            var adapter = GetAdapter(req);
            var data = new byte[(int)size];
            Marshal.Copy(buf, data, 0, (int)size);

            var bytesWritten = adapter.WriteAsync((long)ino, data.AsMemory(), off, CancellationToken.None).GetAwaiter().GetResult();
            Fuse3Native.fuse_reply_write(req, (nuint)bytesWritten);
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnFlush(IntPtr req, ulong ino, IntPtr fi)
    {
        try
        {
            var adapter = GetAdapter(req);
            adapter.FlushAsync((long)ino, CancellationToken.None).GetAwaiter().GetResult();
            Fuse3Native.fuse_reply_err(req, 0);
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnRelease(IntPtr req, ulong ino, IntPtr fi)
    {
        // No-op: adapter cleanup happens at unlink/close.
        Fuse3Native.fuse_reply_err(req, 0);
    }

    private static void OnOpendir(IntPtr req, ulong ino, IntPtr fi)
    {
        try
        {
            var adapter = GetAdapter(req);

            // Validate the inode is a directory
            var attrs = adapter.GetattrAsync((long)ino, CancellationToken.None).GetAwaiter().GetResult();
            if (attrs.Type != InodeType.Directory)
            {
                Fuse3Native.fuse_reply_err(req, Fuse3Native.ENOTDIR);
                return;
            }

            // Store inode number in fi.fh
            var fileInfo = Marshal.PtrToStructure<Fuse3Native.FuseFileInfo>(fi);
            fileInfo.fh = ino;
            Marshal.StructureToPtr(fileInfo, fi, false);

            Fuse3Native.fuse_reply_open(req, ref fileInfo);
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnReaddir(IntPtr req, ulong ino, nuint size, long off, IntPtr fi)
    {
        try
        {
            var adapter = GetAdapter(req);
            var entries = adapter.ReaddirAsync((long)ino, CancellationToken.None).GetAwaiter().GetResult();

            // Allocate output buffer
            var bufSize = (int)size;
            var buf = Marshal.AllocHGlobal(bufSize);

            try
            {
                var currentOff = 0L;
                var remaining = (nuint)bufSize;
                var currentBuf = buf;

                foreach (var entry in entries.Skip((int)off))
                {
                    currentOff++;

                    // Get attributes for readdirplus entry
                    var entryAttrs = adapter.GetattrAsync(entry.InodeNumber, CancellationToken.None).GetAwaiter().GetResult();
                    var entryParam = new Fuse3Native.FuseEntryParam
                    {
                        ino = (ulong)entry.InodeNumber,
                        generation = 1,
                        attr = ToStat(entryAttrs),
                        attr_timeout = 1.0,
                        entry_timeout = 1.0
                    };

                    var entrySize = Fuse3Native.fuse_add_direntry_plus(req, currentBuf, remaining, entry.Name, ref entryParam, off + currentOff);

                    if (entrySize > remaining)
                    {
                        // Buffer full, stop adding entries
                        break;
                    }

                    currentBuf = IntPtr.Add(currentBuf, (int)entrySize);
                    remaining -= entrySize;
                }

                var totalWritten = (nuint)((long)currentBuf - (long)buf);
                Fuse3Native.fuse_reply_buf(req, buf, totalWritten);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnReleasedir(IntPtr req, ulong ino, IntPtr fi)
    {
        // No-op: directory handles are stateless in VDE.
        Fuse3Native.fuse_reply_err(req, 0);
    }

    private static void OnStatfs(IntPtr req, ulong ino)
    {
        try
        {
            var adapter = GetAdapter(req);
            var stats = adapter.StatfsAsync(CancellationToken.None).GetAwaiter().GetResult();

            var stbuf = new Fuse3Native.Statvfs
            {
                f_bsize = (ulong)stats.BlockSize,
                f_frsize = (ulong)stats.BlockSize,
                f_blocks = (ulong)stats.TotalBlocks,
                f_bfree = (ulong)stats.FreeBlocks,
                f_bavail = (ulong)stats.FreeBlocks,
                f_files = (ulong)stats.AllocatedInodes,
                f_ffree = ulong.MaxValue, // VDE supports dynamic inode allocation
                f_favail = ulong.MaxValue,
                f_fsid = 0x56444530, // "VDE0" in hex
                f_flag = 0,
                f_namemax = (ulong)stats.MaxNameLength
            };

            Fuse3Native.fuse_reply_statfs(req, ref stbuf);
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnCreate(IntPtr req, ulong parent, string name, uint mode, IntPtr fi)
    {
        try
        {
            var adapter = GetAdapter(req);
            var permissions = (InodePermissions)(mode & 0x1FF);
            var ino = adapter.CreateAsync((long)parent, name, InodeType.File, permissions, CancellationToken.None).GetAwaiter().GetResult();

            var attrs = adapter.GetattrAsync(ino, CancellationToken.None).GetAwaiter().GetResult();
            var entry = new Fuse3Native.FuseEntryParam
            {
                ino = (ulong)ino,
                generation = 1,
                attr = ToStat(attrs),
                attr_timeout = 1.0,
                entry_timeout = 1.0
            };

            // Set file handle to inode number
            var fileInfo = Marshal.PtrToStructure<Fuse3Native.FuseFileInfo>(fi);
            fileInfo.fh = (ulong)ino;
            Marshal.StructureToPtr(fileInfo, fi, false);

            Fuse3Native.fuse_reply_create(req, ref entry, ref fileInfo);
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnSetxattr(IntPtr req, ulong ino, string name, IntPtr value, nuint size, int flags)
    {
        try
        {
            var adapter = GetAdapter(req);
            var data = new byte[(int)size];
            if (size > 0)
                Marshal.Copy(value, data, 0, (int)size);

            adapter.SetXattrAsync((long)ino, name, data, CancellationToken.None).GetAwaiter().GetResult();
            Fuse3Native.fuse_reply_err(req, 0);
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnGetxattr(IntPtr req, ulong ino, string name, nuint size)
    {
        try
        {
            var adapter = GetAdapter(req);
            var data = adapter.GetXattrAsync((long)ino, name, CancellationToken.None).GetAwaiter().GetResult();

            if (data == null)
            {
                Fuse3Native.fuse_reply_err(req, Fuse3Native.ENODATA);
                return;
            }

            if (size == 0)
            {
                // Return the size of the attribute value
                Fuse3Native.fuse_reply_xattr(req, (nuint)data.Length);
            }
            else
            {
                unsafe
                {
                    fixed (byte* bufPtr = data)
                    {
                        Fuse3Native.fuse_reply_buf(req, (IntPtr)bufPtr, (nuint)data.Length);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnListxattr(IntPtr req, ulong ino, nuint size)
    {
        try
        {
            var adapter = GetAdapter(req);
            var names = adapter.ListXattrAsync((long)ino, CancellationToken.None).GetAwaiter().GetResult();

            // Build null-separated name list as per POSIX xattr convention
            var sb = new StringBuilder();
            foreach (var n in names)
            {
                sb.Append(n);
                sb.Append('\0');
            }

            var data = Encoding.UTF8.GetBytes(sb.ToString());

            if (size == 0)
            {
                Fuse3Native.fuse_reply_xattr(req, (nuint)data.Length);
            }
            else
            {
                unsafe
                {
                    fixed (byte* bufPtr = data)
                    {
                        Fuse3Native.fuse_reply_buf(req, (IntPtr)bufPtr, (nuint)data.Length);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Fuse3Native.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }
}
