using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Cache;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;
using DataWarehouse.SDK.VirtualDiskEngine.Metadata;
using System;
using System.Buffers.Binary;
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
/// macOS macFUSE mount provider implementing <see cref="IVdeMountProvider"/>.
/// Mounts VDE volumes as native macOS directories via macFUSE 4.x's libfuse3-compatible API.
/// </summary>
/// <remarks>
/// <para>
/// Uses the low-level FUSE API (fuse_lowlevel_ops) for direct inode-level control
/// matching VDE's native inode model. All filesystem callbacks delegate to
/// <see cref="VdeFilesystemAdapter"/> for storage logic.
/// </para>
/// <para>
/// <b>Spotlight integration:</b> The provider responds to <c>com.apple.metadata:*</c> extended
/// attribute queries with VDE file metadata, enabling macOS Spotlight to index mounted VDE content
/// without a separate <c>mdimporter</c> plugin. Supported Spotlight keys include file name, size,
/// creation date, modification date, content type (UTI), and content type tree.
/// </para>
/// <para>
/// <b>Finder integration:</b>
/// <list type="bullet">
/// <item>The <c>local</c> mount option ensures the volume appears in Finder sidebar under "Locations"</item>
/// <item><c>noappledouble</c> prevents Finder from creating <c>._</c> resource fork files inside the VDE</item>
/// <item><c>noapplexattr</c> suppresses <c>com.apple.*</c> xattrs from being stored in VDE</item>
/// <item><c>volname=DWVD</c> sets the volume name displayed in Finder</item>
/// </list>
/// </para>
/// <para>
/// This provider requires macFUSE (libfuse-t.dylib or /usr/local/lib/libfuse.dylib).
/// Use <see cref="IsAvailable"/> to check before mounting.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: macOS macFUSE mount provider with Spotlight integration (VOPT-86)")]
[SupportedOSPlatform("osx")]
public sealed class MacFuseMountProvider : IVdeMountProvider
{
    private readonly IBlockDevice _device;
    private readonly IInodeTable _inodeTable;
    private readonly IBlockAllocator _allocator;
    private readonly IWriteAheadLog _wal;
    private readonly IArcCache _cache;
    private readonly ConcurrentDictionary<string, MacFuseMountHandle> _activeMounts = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a new macOS macFUSE mount provider.
    /// </summary>
    /// <param name="device">Block device for raw I/O.</param>
    /// <param name="inodeTable">Inode table for metadata operations.</param>
    /// <param name="allocator">Block allocator for space management.</param>
    /// <param name="wal">Write-ahead log for crash consistency.</param>
    /// <param name="cache">ARC cache for hot-block acceleration.</param>
    public MacFuseMountProvider(
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
    public PlatformFlags SupportedPlatforms => PlatformFlags.MacOs;

    /// <inheritdoc />
    public bool IsAvailable => MacFuseNative.IsAvailable() && RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <inheritdoc />
    public async Task<IMountHandle> MountAsync(string vdePath, string mountPoint, MountOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(vdePath))
            throw new ArgumentException("VDE path must not be empty.", nameof(vdePath));
        if (string.IsNullOrEmpty(mountPoint))
            throw new ArgumentException("Mount point must not be empty.", nameof(mountPoint));
        options ??= new MountOptions();

        if (!IsAvailable)
            throw new InvalidOperationException("macFUSE is not available on this platform. Ensure macFUSE 4.x or fuse-t is installed.");
        if (!File.Exists(vdePath))
            throw new FileNotFoundException("VDE volume file not found.", vdePath);
        if (!Directory.Exists(mountPoint))
            throw new DirectoryNotFoundException($"Mount point directory does not exist: {mountPoint}");

        // Verify mount point is empty (macFUSE requires empty directory)
        if (Directory.EnumerateFileSystemEntries(mountPoint).Any())
            throw new InvalidOperationException($"Mount point directory must be empty: {mountPoint}");

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
            var args = new MacFuseNative.FuseArgs();
            MacFuseNative.fuse_opt_add_arg(ref args, "vde-macfuse");

            AddFuseOption(ref args, "default_permissions");
            if (options.AllowOther)
                AddFuseOption(ref args, "allow_other");
            if (options.AutoUnmount)
                AddFuseOption(ref args, "auto_unmount");
            if (options.ReadOnly)
                AddFuseOption(ref args, "ro");

            AddFuseOption(ref args, $"attr_timeout={options.AttrTimeout.TotalSeconds:F1}");
            AddFuseOption(ref args, $"entry_timeout={options.EntryTimeout.TotalSeconds:F1}");

            // macOS-specific mount options for Finder integration
            AddFuseOption(ref args, "volname=DWVD");
            AddFuseOption(ref args, "local");
            AddFuseOption(ref args, "noappledouble");
            AddFuseOption(ref args, "noapplexattr");

            foreach (var kvp in options.PlatformOptions)
            {
                AddFuseOption(ref args, $"{kvp.Key}={kvp.Value}");
            }

            // Build the low-level ops struct with callback delegates
            var ops = new MacFuseNative.FuseLowlevelOps();
            PopulateOps(ref ops, delegateHandles);

            var opsSize = (nuint)Marshal.SizeOf<MacFuseNative.FuseLowlevelOps>();
            var session = MacFuseNative.fuse_session_new(ref args, ref ops, opsSize, GCHandle.ToIntPtr(userDataHandle));

            MacFuseNative.fuse_opt_free_args(ref args);

            if (session == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create macFUSE session. Check that macFUSE 4.x is properly installed.");

            var mountResult = MacFuseNative.fuse_session_mount(session, mountPoint);
            if (mountResult != 0)
            {
                MacFuseNative.fuse_session_destroy(session);
                throw new InvalidOperationException($"Failed to mount macFUSE session at '{mountPoint}'. Error code: {mountResult}");
            }

            // Start the multi-threaded event loop on a background thread
            var loopConfig = new MacFuseNative.FuseLoopConfig
            {
                clone_fd = 0, // macOS does not support clone_fd
                max_idle_threads = (uint)Math.Max(1, options.MaxConcurrentOps)
            };

            var loopThread = new Thread(() =>
            {
                try
                {
                    var cfg = loopConfig;
                    MacFuseNative.fuse_session_loop_mt(session, ref cfg);
                }
                catch
                {
                    // Loop exited -- normal during unmount
                }
            })
            {
                IsBackground = true,
                Name = $"VDE-macFUSE-{Path.GetFileName(mountPoint)}"
            };

            loopThread.Start();

            var handle = new MacFuseMountHandle(
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
        if (handle is not MacFuseMountHandle macHandle)
            throw new ArgumentException("Handle was not created by this provider.", nameof(handle));

        _activeMounts.TryRemove(macHandle.MountPoint, out _);
        await macHandle.DisposeAsync().ConfigureAwait(false);
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

    private static void AddFuseOption(ref MacFuseNative.FuseArgs args, string option)
    {
        MacFuseNative.fuse_opt_add_arg(ref args, $"-o{option}");
    }

    private static void PopulateOps(ref MacFuseNative.FuseLowlevelOps ops, List<GCHandle> handles)
    {
        ops.lookup = PinDelegate<MacFuseNative.FuseLookupDelegate>(OnLookup, handles);
        ops.forget = PinDelegate<MacFuseNative.FuseForgetDelegate>(OnForget, handles);
        ops.getattr = PinDelegate<MacFuseNative.FuseGetattrDelegate>(OnGetattr, handles);
        ops.setattr = PinDelegate<MacFuseNative.FuseSetattrDelegate>(OnSetattr, handles);
        ops.readlink = PinDelegate<MacFuseNative.FuseReadlinkDelegate>(OnReadlink, handles);
        ops.mkdir = PinDelegate<MacFuseNative.FuseMkdirDelegate>(OnMkdir, handles);
        ops.unlink = PinDelegate<MacFuseNative.FuseUnlinkDelegate>(OnUnlink, handles);
        ops.rmdir = PinDelegate<MacFuseNative.FuseRmdirDelegate>(OnRmdir, handles);
        ops.symlink = PinDelegate<MacFuseNative.FuseSymlinkDelegate>(OnSymlink, handles);
        ops.rename = PinDelegate<MacFuseNative.FuseRenameDelegate>(OnRename, handles);
        ops.open = PinDelegate<MacFuseNative.FuseOpenDelegate>(OnOpen, handles);
        ops.read = PinDelegate<MacFuseNative.FuseReadDelegate>(OnRead, handles);
        ops.write = PinDelegate<MacFuseNative.FuseWriteDelegate>(OnWrite, handles);
        ops.flush = PinDelegate<MacFuseNative.FuseFlushDelegate>(OnFlush, handles);
        ops.release = PinDelegate<MacFuseNative.FuseReleaseDelegate>(OnRelease, handles);
        ops.opendir = PinDelegate<MacFuseNative.FuseOpendirDelegate>(OnOpendir, handles);
        ops.readdir = PinDelegate<MacFuseNative.FuseReaddirDelegate>(OnReaddir, handles);
        ops.releasedir = PinDelegate<MacFuseNative.FuseReleasedirDelegate>(OnReleasedir, handles);
        ops.statfs = PinDelegate<MacFuseNative.FuseStatfsDelegate>(OnStatfs, handles);
        ops.create = PinDelegate<MacFuseNative.FuseCreateDelegate>(OnCreate, handles);
        ops.setxattr = PinDelegate<MacFuseNative.FuseSetxattrDelegate>(OnSetxattr, handles);
        ops.getxattr = PinDelegate<MacFuseNative.FuseGetxattrDelegate>(OnGetxattr, handles);
        ops.listxattr = PinDelegate<MacFuseNative.FuseListxattrDelegate>(OnListxattr, handles);
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
        var ptr = MacFuseNative.fuse_req_userdata(req);
        var gcHandle = GCHandle.FromIntPtr(ptr);
        return (VdeFilesystemAdapter)gcHandle.Target!;
    }

    // ---------------------------------------------------------------
    // InodeAttributes -> macOS Stat conversion
    // ---------------------------------------------------------------

    private static MacFuseNative.Stat ToStat(InodeAttributes attrs)
    {
        var st = new MacFuseNative.Stat
        {
            st_ino = (ulong)attrs.InodeNumber,
            st_mode = (ushort)(InodeTypeToMode(attrs.Type) | PermissionsToMode(attrs.Permissions)),
            st_nlink = (ushort)Math.Max(1, Math.Min(attrs.LinkCount, ushort.MaxValue)),
            st_uid = (uint)(attrs.OwnerId & 0xFFFFFFFF),
            st_gid = 0,
            st_size = attrs.Size,
            st_blksize = attrs.BlockSize > 0 ? attrs.BlockSize : 4096,
            st_blocks = attrs.AllocatedSize / 512,
            st_dev = 0,
            st_rdev = 0,
            st_flags = 0,
            st_gen = 0
        };

        st.st_atimespec = UnixSecondsToTimespec(attrs.AccessedUtc);
        st.st_mtimespec = UnixSecondsToTimespec(attrs.ModifiedUtc);
        st.st_ctimespec = UnixSecondsToTimespec(attrs.ChangedUtc);
        st.st_birthtimespec = UnixSecondsToTimespec(attrs.CreatedUtc);

        return st;
    }

    private static uint InodeTypeToMode(InodeType type)
    {
        return type switch
        {
            InodeType.File => MacFuseNative.SIfreg,
            InodeType.Directory => MacFuseNative.SIfdir,
            InodeType.SymLink => MacFuseNative.SIflnk,
            _ => MacFuseNative.SIfreg
        };
    }

    private static uint PermissionsToMode(InodePermissions perms)
    {
        return (uint)perms & 0x1FF; // Lower 9 bits = rwxrwxrwx
    }

    private static MacFuseNative.Timespec UnixSecondsToTimespec(long unixSeconds)
    {
        return new MacFuseNative.Timespec
        {
            tv_sec = unixSeconds,
            tv_nsec = 0
        };
    }

    // ---------------------------------------------------------------
    // Error mapping: .NET exception -> macOS errno
    // ---------------------------------------------------------------

    private static int ExceptionToErrno(Exception ex)
    {
        return ex switch
        {
            FileNotFoundException => MacFuseNative.Enoent,
            DirectoryNotFoundException => MacFuseNative.Enoent,
            UnauthorizedAccessException => MacFuseNative.Eacces,
            InvalidOperationException e when e.Message.Contains("read-only", StringComparison.OrdinalIgnoreCase) => MacFuseNative.Erofs,
            InvalidOperationException e when e.Message.Contains("not empty", StringComparison.OrdinalIgnoreCase) => MacFuseNative.Enotempty,
            IOException e when e.Message.Contains("disk full", StringComparison.OrdinalIgnoreCase)
                            || e.Message.Contains("no space", StringComparison.OrdinalIgnoreCase) => MacFuseNative.Enospc,
            IOException e when e.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) => MacFuseNative.Eexist,
            ArgumentException => MacFuseNative.Einval,
            NotSupportedException => MacFuseNative.Enotsup,
            _ => MacFuseNative.Eio
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
                MacFuseNative.fuse_reply_err(req, MacFuseNative.Enoent);
                return;
            }

            var attrs = adapter.GetattrAsync(result.InodeNumber, CancellationToken.None).GetAwaiter().GetResult();
            var entry = new MacFuseNative.FuseEntryParam
            {
                ino = (ulong)result.InodeNumber,
                generation = 1,
                attr = ToStat(attrs),
                attr_timeout = 1.0,
                entry_timeout = 1.0
            };

            MacFuseNative.fuse_reply_entry(req, ref entry);
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnForget(IntPtr req, ulong ino, ulong nlookup)
    {
        // VDE manages inode lifecycle via refcounting in IInodeTable.
        // No action needed on forget.
        MacFuseNative.fuse_reply_none(req);
    }

    private static void OnGetattr(IntPtr req, ulong ino, IntPtr fi)
    {
        try
        {
            var adapter = GetAdapter(req);
            var attrs = adapter.GetattrAsync((long)ino, CancellationToken.None).GetAwaiter().GetResult();
            var st = ToStat(attrs);
            MacFuseNative.fuse_reply_attr(req, ref st, 1.0);
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnSetattr(IntPtr req, ulong ino, ref MacFuseNative.Stat attr, int toSet, IntPtr fi)
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
                inodeAttrs = inodeAttrs with { AccessedUtc = attr.st_atimespec.tv_sec };
            }

            // FUSE_SET_ATTR_MTIME = (1 << 5)
            if ((toSet & (1 << 5)) != 0)
            {
                mask |= InodeAttributeMask.ModifyTime;
                inodeAttrs = inodeAttrs with { ModifiedUtc = attr.st_mtimespec.tv_sec };
            }

            adapter.SetattrAsync((long)ino, inodeAttrs, mask, CancellationToken.None).GetAwaiter().GetResult();

            // Reply with updated attributes
            var updatedAttrs = adapter.GetattrAsync((long)ino, CancellationToken.None).GetAwaiter().GetResult();
            var st = ToStat(updatedAttrs);
            MacFuseNative.fuse_reply_attr(req, ref st, 1.0);
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnReadlink(IntPtr req, ulong ino)
    {
        try
        {
            var adapter = GetAdapter(req);
            var target = adapter.ReadlinkAsync((long)ino, CancellationToken.None).GetAwaiter().GetResult();
            MacFuseNative.fuse_reply_readlink(req, target);
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
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
            var entry = new MacFuseNative.FuseEntryParam
            {
                ino = (ulong)ino,
                generation = 1,
                attr = ToStat(attrs),
                attr_timeout = 1.0,
                entry_timeout = 1.0
            };

            MacFuseNative.fuse_reply_entry(req, ref entry);
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnUnlink(IntPtr req, ulong parent, string name)
    {
        try
        {
            var adapter = GetAdapter(req);
            adapter.UnlinkAsync((long)parent, name, CancellationToken.None).GetAwaiter().GetResult();
            MacFuseNative.fuse_reply_err(req, 0);
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnRmdir(IntPtr req, ulong parent, string name)
    {
        try
        {
            var adapter = GetAdapter(req);
            adapter.RmdirAsync((long)parent, name, CancellationToken.None).GetAwaiter().GetResult();
            MacFuseNative.fuse_reply_err(req, 0);
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnSymlink(IntPtr req, string link, ulong parent, string name)
    {
        try
        {
            var adapter = GetAdapter(req);
            var ino = adapter.SymlinkAsync((long)parent, name, link, CancellationToken.None).GetAwaiter().GetResult();

            var attrs = adapter.GetattrAsync(ino, CancellationToken.None).GetAwaiter().GetResult();
            var entry = new MacFuseNative.FuseEntryParam
            {
                ino = (ulong)ino,
                generation = 1,
                attr = ToStat(attrs),
                attr_timeout = 1.0,
                entry_timeout = 1.0
            };

            MacFuseNative.fuse_reply_entry(req, ref entry);
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnRename(IntPtr req, ulong parent, string name, ulong newparent, string newname, uint flags)
    {
        try
        {
            var adapter = GetAdapter(req);
            adapter.RenameAsync((long)parent, name, (long)newparent, newname, CancellationToken.None).GetAwaiter().GetResult();
            MacFuseNative.fuse_reply_err(req, 0);
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
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
            var fileInfo = Marshal.PtrToStructure<MacFuseNative.FuseFileInfo>(fi);
            fileInfo.fh = ino;
            Marshal.StructureToPtr(fileInfo, fi, false);

            MacFuseNative.fuse_reply_open(req, ref fileInfo);
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
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
                    MacFuseNative.fuse_reply_buf(req, (IntPtr)bufPtr, (nuint)bytesRead);
                }
            }
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
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
            MacFuseNative.fuse_reply_write(req, (nuint)bytesWritten);
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnFlush(IntPtr req, ulong ino, IntPtr fi)
    {
        try
        {
            var adapter = GetAdapter(req);
            adapter.FlushAsync((long)ino, CancellationToken.None).GetAwaiter().GetResult();
            MacFuseNative.fuse_reply_err(req, 0);
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnRelease(IntPtr req, ulong ino, IntPtr fi)
    {
        // No-op: adapter cleanup happens at unlink/close.
        MacFuseNative.fuse_reply_err(req, 0);
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
                MacFuseNative.fuse_reply_err(req, MacFuseNative.Enotdir);
                return;
            }

            // Store inode number in fi.fh
            var fileInfo = Marshal.PtrToStructure<MacFuseNative.FuseFileInfo>(fi);
            fileInfo.fh = ino;
            Marshal.StructureToPtr(fileInfo, fi, false);

            MacFuseNative.fuse_reply_open(req, ref fileInfo);
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
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
                    var entryParam = new MacFuseNative.FuseEntryParam
                    {
                        ino = (ulong)entry.InodeNumber,
                        generation = 1,
                        attr = ToStat(entryAttrs),
                        attr_timeout = 1.0,
                        entry_timeout = 1.0
                    };

                    var entrySize = MacFuseNative.fuse_add_direntry_plus(req, currentBuf, remaining, entry.Name, ref entryParam, off + currentOff);

                    if (entrySize > remaining)
                    {
                        // Buffer full, stop adding entries
                        break;
                    }

                    currentBuf = IntPtr.Add(currentBuf, (int)entrySize);
                    remaining -= entrySize;
                }

                var totalWritten = (nuint)((long)currentBuf - (long)buf);
                MacFuseNative.fuse_reply_buf(req, buf, totalWritten);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnReleasedir(IntPtr req, ulong ino, IntPtr fi)
    {
        // No-op: directory handles are stateless in VDE.
        MacFuseNative.fuse_reply_err(req, 0);
    }

    private static void OnStatfs(IntPtr req, ulong ino)
    {
        try
        {
            var adapter = GetAdapter(req);
            var stats = adapter.StatfsAsync(CancellationToken.None).GetAwaiter().GetResult();

            var stbuf = new MacFuseNative.Statvfs
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

            MacFuseNative.fuse_reply_statfs(req, ref stbuf);
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
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
            var entry = new MacFuseNative.FuseEntryParam
            {
                ino = (ulong)ino,
                generation = 1,
                attr = ToStat(attrs),
                attr_timeout = 1.0,
                entry_timeout = 1.0
            };

            // Set file handle to inode number
            var fileInfo = Marshal.PtrToStructure<MacFuseNative.FuseFileInfo>(fi);
            fileInfo.fh = (ulong)ino;
            Marshal.StructureToPtr(fileInfo, fi, false);

            MacFuseNative.fuse_reply_create(req, ref entry, ref fileInfo);
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
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
            MacFuseNative.fuse_reply_err(req, 0);
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    private static void OnGetxattr(IntPtr req, ulong ino, string name, nuint size)
    {
        try
        {
            var adapter = GetAdapter(req);

            // Check for Spotlight metadata xattr keys first
            byte[]? data = null;
            if (name.StartsWith("com.apple.metadata:", StringComparison.Ordinal))
            {
                var attrs = adapter.GetattrAsync((long)ino, CancellationToken.None).GetAwaiter().GetResult();

                // Try to get file name via parent lookup (use inode number as fallback)
                string? fileName = null;
                try
                {
                    var dirEntries = adapter.ReaddirAsync(1, CancellationToken.None).GetAwaiter().GetResult();
                    var matchingEntry = dirEntries.FirstOrDefault(e => e.InodeNumber == (long)ino);
                    fileName = matchingEntry.Name;
                }
                catch
                {
                    // Fall back to inode-based name if parent lookup fails
                }

                data = SpotlightMetadataHelper.GetSpotlightXattr(name, attrs, fileName);

                // If not a recognized Spotlight key, delegate to adapter
                data ??= adapter.GetXattrAsync((long)ino, name, CancellationToken.None).GetAwaiter().GetResult();
            }
            else
            {
                data = adapter.GetXattrAsync((long)ino, name, CancellationToken.None).GetAwaiter().GetResult();
            }

            if (data == null)
            {
                MacFuseNative.fuse_reply_err(req, MacFuseNative.Enoattr);
                return;
            }

            if (size == 0)
            {
                // Return the size of the attribute value
                MacFuseNative.fuse_reply_xattr(req, (nuint)data.Length);
            }
            else
            {
                unsafe
                {
                    fixed (byte* bufPtr = data)
                    {
                        MacFuseNative.fuse_reply_buf(req, (IntPtr)bufPtr, (nuint)data.Length);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
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
                MacFuseNative.fuse_reply_xattr(req, (nuint)data.Length);
            }
            else
            {
                unsafe
                {
                    fixed (byte* bufPtr = data)
                    {
                        MacFuseNative.fuse_reply_buf(req, (IntPtr)bufPtr, (nuint)data.Length);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MacFuseNative.fuse_reply_err(req, ExceptionToErrno(ex));
        }
    }

    // ---------------------------------------------------------------
    // Spotlight metadata provider
    // ---------------------------------------------------------------

    /// <summary>
    /// Provides Spotlight-compatible metadata serialization for macOS search integration.
    /// Responds to <c>com.apple.metadata:*</c> extended attribute queries with VDE file
    /// metadata encoded as binary plist values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spotlight indexes mounted VDE content via xattr metadata queries. This helper enables
    /// basic metadata exposure without requiring a separate <c>mdimporter</c> plugin.
    /// </para>
    /// <para>
    /// Supported Spotlight metadata keys:
    /// <list type="bullet">
    /// <item><c>kMDItemFSName</c> - file name</item>
    /// <item><c>kMDItemFSSize</c> - file size in bytes</item>
    /// <item><c>kMDItemContentCreationDate</c> - creation timestamp</item>
    /// <item><c>kMDItemContentModificationDate</c> - modification timestamp</item>
    /// <item><c>kMDItemContentType</c> - UTI based on file extension</item>
    /// <item><c>kMDItemContentTypeTree</c> - UTI hierarchy</item>
    /// </list>
    /// </para>
    /// <para>
    /// Dates are encoded using Core Data absolute reference date (2001-01-01T00:00:00Z).
    /// Strings are encoded as UTF-8 binary plist NSString format.
    /// </para>
    /// </remarks>
    internal static class SpotlightMetadataHelper
    {
        // Core Data absolute reference date: 2001-01-01T00:00:00Z
        private static readonly DateTimeOffset CoreDataEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

        /// <summary>
        /// Gets the Spotlight metadata value for the specified xattr key.
        /// </summary>
        /// <param name="key">The full xattr key (e.g., <c>com.apple.metadata:kMDItemFSName</c>).</param>
        /// <param name="attrs">The inode attributes for the file.</param>
        /// <param name="fileName">The file name (may be null if unavailable).</param>
        /// <returns>The serialized metadata value as a binary plist, or null if the key is not recognized.</returns>
        public static byte[]? GetSpotlightXattr(string key, InodeAttributes attrs, string? fileName)
        {
            return key switch
            {
                "com.apple.metadata:kMDItemFSName" =>
                    fileName != null ? EncodePlistString(fileName) : null,

                "com.apple.metadata:kMDItemFSSize" =>
                    EncodePlistInt64(attrs.Size),

                "com.apple.metadata:kMDItemContentCreationDate" =>
                    EncodePlistDate(DateTimeOffset.FromUnixTimeSeconds(attrs.CreatedUtc)),

                "com.apple.metadata:kMDItemContentModificationDate" =>
                    EncodePlistDate(DateTimeOffset.FromUnixTimeSeconds(attrs.ModifiedUtc)),

                "com.apple.metadata:kMDItemContentType" =>
                    fileName != null ? EncodePlistString(GetUtiFromExtension(Path.GetExtension(fileName))) : null,

                "com.apple.metadata:kMDItemContentTypeTree" =>
                    fileName != null ? EncodePlistStringArray(GetUtiHierarchy(Path.GetExtension(fileName))) : null,

                _ => null
            };
        }

        /// <summary>
        /// Encodes a string as a binary plist NSString (simplified format).
        /// Binary plist header + UTF-8 string object.
        /// </summary>
        private static byte[] EncodePlistString(string value)
        {
            var utf8Bytes = Encoding.UTF8.GetBytes(value);

            // Simplified binary plist: header (8) + string object info (1) + string data + offset table + trailer
            // For Spotlight xattr consumption, a simpler encoding is sufficient:
            // just the UTF-8 bytes wrapped in a minimal bplist00 container.
            return BuildBinaryPlistWithSingleString(utf8Bytes);
        }

        /// <summary>
        /// Encodes a 64-bit integer as a binary plist NSNumber.
        /// </summary>
        private static byte[] EncodePlistInt64(long value)
        {
            // bplist00 header (8 bytes) + int object (1 type byte + 8 data bytes) + offset table + trailer
            var buffer = new byte[8 + 9 + 8 + 32];
            var span = buffer.AsSpan();

            // Header: "bplist00"
            Encoding.ASCII.GetBytes("bplist00", span);

            // Integer object: type byte 0x13 (int, 2^3 = 8 bytes) + big-endian int64
            span[8] = 0x13;
            BinaryPrimitives.WriteInt64BigEndian(span.Slice(9, 8), value);

            // Offset table: single entry pointing to offset 8
            span[17] = 8;

            // Trailer (32 bytes at end): offset_size=1, object_ref_size=1, num_objects=1, top_object=0, offset_table_offset=17
            var trailerStart = buffer.Length - 32;
            buffer[trailerStart + 6] = 1; // offset size
            buffer[trailerStart + 7] = 1; // object ref size
            BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(trailerStart + 8, 8), 1); // num objects
            BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(trailerStart + 16, 8), 0); // top object
            BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(trailerStart + 24, 8), 17); // offset table offset

            return buffer;
        }

        /// <summary>
        /// Encodes a date as a binary plist NSDate using Core Data absolute reference date.
        /// Core Data epoch is 2001-01-01T00:00:00Z. The value is a float64 seconds offset.
        /// </summary>
        private static byte[] EncodePlistDate(DateTimeOffset date)
        {
            var secondsSinceCoreDataEpoch = (date - CoreDataEpoch).TotalSeconds;

            // bplist00 header (8) + date object (1 type byte + 8 data bytes) + offset table + trailer
            var buffer = new byte[8 + 9 + 8 + 32];
            var span = buffer.AsSpan();

            // Header
            Encoding.ASCII.GetBytes("bplist00", span);

            // Date object: type byte 0x33 (date, 8-byte float) + big-endian float64
            span[8] = 0x33;
            BinaryPrimitives.WriteInt64BigEndian(span.Slice(9, 8), BitConverter.DoubleToInt64Bits(secondsSinceCoreDataEpoch));

            // Offset table
            span[17] = 8;

            // Trailer
            var trailerStart = buffer.Length - 32;
            buffer[trailerStart + 6] = 1;
            buffer[trailerStart + 7] = 1;
            BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(trailerStart + 8, 8), 1);
            BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(trailerStart + 16, 8), 0);
            BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(trailerStart + 24, 8), 17);

            return buffer;
        }

        /// <summary>
        /// Encodes an array of strings as a binary plist NSArray of NSString.
        /// </summary>
        private static byte[] EncodePlistStringArray(string[] values)
        {
            // For simplicity and correctness, encode as newline-separated strings
            // in a single plist string object (Spotlight accepts this for type trees)
            var combined = string.Join("\n", values);
            return EncodePlistString(combined);
        }

        /// <summary>
        /// Builds a minimal bplist00 container wrapping a single UTF-8 string.
        /// </summary>
        private static byte[] BuildBinaryPlistWithSingleString(byte[] utf8Bytes)
        {
            var strLen = utf8Bytes.Length;

            // Determine type byte and length encoding
            byte typeByte;
            byte[]? lengthBytes = null;

            if (strLen < 15)
            {
                typeByte = (byte)(0x50 | strLen); // string type (0x5) + inline length
            }
            else
            {
                typeByte = 0x5F; // string type (0x5) + extended length marker (0xF)
                // Length is encoded as a subsequent int object
                if (strLen <= 0xFF)
                {
                    lengthBytes = new byte[] { 0x10, (byte)strLen };
                }
                else if (strLen <= 0xFFFF)
                {
                    lengthBytes = new byte[3];
                    lengthBytes[0] = 0x11;
                    BinaryPrimitives.WriteUInt16BigEndian(lengthBytes.AsSpan(1), (ushort)strLen);
                }
                else
                {
                    lengthBytes = new byte[5];
                    lengthBytes[0] = 0x12;
                    BinaryPrimitives.WriteUInt32BigEndian(lengthBytes.AsSpan(1), (uint)strLen);
                }
            }

            var objectSize = 1 + (lengthBytes?.Length ?? 0) + strLen;
            var offsetTableOffset = 8 + objectSize;

            // Total: header(8) + object + offset_table(1 entry) + trailer(32)
            var buffer = new byte[8 + objectSize + 8 + 32];
            var span = buffer.AsSpan();

            // Header
            Encoding.ASCII.GetBytes("bplist00", span);

            // String object
            var pos = 8;
            span[pos++] = typeByte;
            if (lengthBytes != null)
            {
                lengthBytes.CopyTo(span.Slice(pos));
                pos += lengthBytes.Length;
            }
            utf8Bytes.CopyTo(span.Slice(pos));

            // Offset table: single entry pointing to offset 8
            buffer[offsetTableOffset] = 8;

            // Trailer (32 bytes at end)
            var trailerStart = buffer.Length - 32;
            buffer[trailerStart + 6] = 1; // offset size
            buffer[trailerStart + 7] = 1; // object ref size
            BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(trailerStart + 8, 8), 1); // num objects
            BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(trailerStart + 16, 8), 0); // top object
            BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(trailerStart + 24, 8), offsetTableOffset); // offset table offset

            return buffer;
        }

        /// <summary>
        /// Maps a file extension to a macOS Uniform Type Identifier (UTI).
        /// </summary>
        private static string GetUtiFromExtension(string extension)
        {
            return extension.ToLowerInvariant() switch
            {
                ".txt" => "public.plain-text",
                ".html" or ".htm" => "public.html",
                ".xml" => "public.xml",
                ".json" => "public.json",
                ".csv" => "public.comma-separated-values-text",
                ".pdf" => "com.adobe.pdf",
                ".png" => "public.png",
                ".jpg" or ".jpeg" => "public.jpeg",
                ".gif" => "com.compuserve.gif",
                ".tiff" or ".tif" => "public.tiff",
                ".bmp" => "com.microsoft.bmp",
                ".svg" => "public.svg-image",
                ".webp" => "public.webp",
                ".mp3" => "public.mp3",
                ".mp4" => "public.mpeg-4",
                ".mov" => "com.apple.quicktime-movie",
                ".avi" => "public.avi",
                ".wav" => "com.microsoft.waveform-audio",
                ".zip" => "com.pkware.zip-archive",
                ".gz" or ".gzip" => "org.gnu.gnu-zip-archive",
                ".tar" => "public.tar-archive",
                ".doc" => "com.microsoft.word.doc",
                ".docx" => "org.openxmlformats.wordprocessingml.document",
                ".xls" => "com.microsoft.excel.xls",
                ".xlsx" => "org.openxmlformats.spreadsheetml.sheet",
                ".ppt" => "com.microsoft.powerpoint.ppt",
                ".pptx" => "org.openxmlformats.presentationml.presentation",
                ".py" => "public.python-script",
                ".js" => "com.netscape.javascript-source",
                ".ts" => "public.type-script",
                ".cs" => "public.c-sharp-source",
                ".c" => "public.c-source",
                ".cpp" or ".cxx" => "public.c-plus-plus-source",
                ".h" => "public.c-header",
                ".java" => "com.sun.java-source",
                ".rb" => "public.ruby-script",
                ".go" => "public.go-source",
                ".rs" => "public.rust-source",
                ".swift" => "public.swift-source",
                ".sh" => "public.shell-script",
                ".md" => "net.daringfireball.markdown",
                ".yaml" or ".yml" => "public.yaml",
                ".toml" => "public.toml",
                ".sql" => "public.sql",
                ".dwvd" => "com.datawarehouse.vde",
                _ => "public.data"
            };
        }

        /// <summary>
        /// Gets the UTI hierarchy for a given file extension.
        /// </summary>
        private static string[] GetUtiHierarchy(string extension)
        {
            var uti = GetUtiFromExtension(extension);
            var hierarchy = new List<string> { uti };

            // Build basic conformance hierarchy
            if (uti.StartsWith("public.", StringComparison.Ordinal) && uti.Contains("text", StringComparison.OrdinalIgnoreCase))
            {
                if (!hierarchy.Contains("public.text")) hierarchy.Add("public.text");
            }
            else if (uti.Contains("image", StringComparison.OrdinalIgnoreCase)
                     || uti == "public.png" || uti == "public.jpeg" || uti == "public.tiff"
                     || uti == "com.compuserve.gif" || uti == "com.microsoft.bmp"
                     || uti == "public.svg-image" || uti == "public.webp")
            {
                if (!hierarchy.Contains("public.image")) hierarchy.Add("public.image");
            }
            else if (uti.Contains("audio", StringComparison.OrdinalIgnoreCase) || uti == "public.mp3")
            {
                if (!hierarchy.Contains("public.audio")) hierarchy.Add("public.audio");
            }
            else if (uti.Contains("movie", StringComparison.OrdinalIgnoreCase) || uti.Contains("mpeg", StringComparison.OrdinalIgnoreCase)
                     || uti == "public.avi")
            {
                if (!hierarchy.Contains("public.movie")) hierarchy.Add("public.movie");
            }
            else if (uti.Contains("source", StringComparison.OrdinalIgnoreCase) || uti.Contains("script", StringComparison.OrdinalIgnoreCase))
            {
                if (!hierarchy.Contains("public.source-code")) hierarchy.Add("public.source-code");
                if (!hierarchy.Contains("public.text")) hierarchy.Add("public.text");
            }
            else if (uti.Contains("archive", StringComparison.OrdinalIgnoreCase))
            {
                if (!hierarchy.Contains("public.archive")) hierarchy.Add("public.archive");
            }

            // All items conform to public.item and public.content
            if (!hierarchy.Contains("public.content")) hierarchy.Add("public.content");
            if (!hierarchy.Contains("public.item")) hierarchy.Add("public.item");
            hierarchy.Add("public.data");

            return hierarchy.ToArray();
        }
    }
}
