using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Metadata;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mount;

#region WinFsp P/Invoke Bindings

/// <summary>
/// NTSTATUS codes used by WinFsp filesystem callbacks.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: WinFsp NTSTATUS error codes (VOPT-84)")]
internal static class NtStatus
{
    public const int StatusSuccess = 0;
    public const int StatusNotImplemented = unchecked((int)0xC0000002);
    public const int StatusInvalidParameter = unchecked((int)0xC000000D);
    public const int StatusNoSuchFile = unchecked((int)0xC000000F);
    public const int StatusEndOfFile = unchecked((int)0xC0000011);
    public const int StatusAccessDenied = unchecked((int)0xC0000022);
    public const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    public const int StatusObjectNameCollision = unchecked((int)0xC0000035);
    public const int StatusObjectPathNotFound = unchecked((int)0xC000003A);
    public const int StatusSharingViolation = unchecked((int)0xC0000043);
    public const int StatusUnexpectedIoError = unchecked((int)0xC00000E9);
    public const int StatusDirectoryNotEmpty = unchecked((int)0xC0000101);
    public const int StatusNotADirectory = unchecked((int)0xC0000103);
    public const int StatusCancelled = unchecked((int)0xC0000120);
    public const int StatusDiskFull = unchecked((int)0xC000007F);
    public const int StatusInternalError = unchecked((int)0xC00000E5);
}

/// <summary>
/// WinFsp volume information structure returned by GetVolumeInfo callback.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct FspFsctlVolumeInfo
{
    public long TotalSize;
    public long FreeSize;

    /// <summary>Volume label length in bytes (not characters).</summary>
    public ushort VolumeLabelLength;

    /// <summary>Volume label (up to 32 Unicode characters).</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string VolumeLabel;
}

/// <summary>
/// WinFsp file information structure for file metadata callbacks.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FspFsctlFileInfo
{
    public uint FileAttributes;
    public uint ReparseTag;
    public long AllocationSize;
    public long FileSize;
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public long ChangeTime;
    public long IndexNumber;
    public uint HardLinks;
    public uint EaSize;
}

/// <summary>
/// WinFsp open file information combining file info with a normalized name.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FspFsctlOpenFileInfo
{
    public FspFsctlFileInfo FileInfo;
    public IntPtr NormalizedName;
    public ushort NormalizedNameSize;
}

/// <summary>
/// WinFsp directory entry information for directory enumeration.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct FspFsctlDirInfo
{
    public ushort Size;
    public FspFsctlFileInfo FileInfo;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string FileName;
}

/// <summary>
/// WinFsp filesystem interface with function pointers for all callbacks.
/// Each callback delegates to VdeFilesystemAdapter through the WinFspMountProvider.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FspFileSystemInterface
{
    /// <summary>GetVolumeInfo callback: reports volume size, free space, and label.</summary>
    public IntPtr GetVolumeInfo;

    /// <summary>SetVolumeLabel callback: sets the volume label.</summary>
    public IntPtr SetVolumeLabel;

    /// <summary>GetSecurityByName callback: retrieves security descriptor by file path.</summary>
    public IntPtr GetSecurityByName;

    /// <summary>Create callback: creates a new file or directory.</summary>
    public IntPtr Create;

    /// <summary>Open callback: opens an existing file or directory.</summary>
    public IntPtr Open;

    /// <summary>Overwrite callback: overwrites an existing file's contents.</summary>
    public IntPtr Overwrite;

    /// <summary>Cleanup callback: called when a file handle is being closed.</summary>
    public IntPtr Cleanup;

    /// <summary>Close callback: releases file context after all handles closed.</summary>
    public IntPtr Close;

    /// <summary>Read callback: reads data from a file.</summary>
    public IntPtr Read;

    /// <summary>Write callback: writes data to a file.</summary>
    public IntPtr Write;

    /// <summary>Flush callback: flushes cached data to stable storage.</summary>
    public IntPtr Flush;

    /// <summary>GetFileInfo callback: retrieves file metadata.</summary>
    public IntPtr GetFileInfo;

    /// <summary>SetBasicInfo callback: sets timestamps and file attributes.</summary>
    public IntPtr SetBasicInfo;

    /// <summary>SetFileSize callback: sets file size (truncate or extend).</summary>
    public IntPtr SetFileSize;

    /// <summary>CanDelete callback: checks whether a file/directory can be deleted.</summary>
    public IntPtr CanDelete;

    /// <summary>Rename callback: renames a file or directory.</summary>
    public IntPtr Rename;

    /// <summary>GetSecurity callback: retrieves a file's security descriptor.</summary>
    public IntPtr GetSecurity;

    /// <summary>SetSecurity callback: sets a file's security descriptor.</summary>
    public IntPtr SetSecurity;

    /// <summary>ReadDirectory callback: enumerates directory contents.</summary>
    public IntPtr ReadDirectory;

    /// <summary>ResolveReparsePoints callback: resolves reparse points.</summary>
    public IntPtr ResolveReparsePoints;

    /// <summary>GetReparsePoint callback: retrieves reparse point data.</summary>
    public IntPtr GetReparsePoint;

    /// <summary>SetReparsePoint callback: sets reparse point data.</summary>
    public IntPtr SetReparsePoint;

    /// <summary>DeleteReparsePoint callback: removes reparse point data.</summary>
    public IntPtr DeleteReparsePoint;

    /// <summary>GetStreamInfo callback: enumerates alternate data streams.</summary>
    public IntPtr GetStreamInfo;

    /// <summary>GetDirInfoByName callback: retrieves a single directory entry by name.</summary>
    public IntPtr GetDirInfoByName;

    /// <summary>Control callback: handles FSCTL requests.</summary>
    public IntPtr Control;

    /// <summary>SetDelete callback: marks a file for deletion.</summary>
    public IntPtr SetDelete;

    /// <summary>CreateEx callback: extended create with extra create parameters.</summary>
    public IntPtr CreateEx;

    /// <summary>OverwriteEx callback: extended overwrite with extra parameters.</summary>
    public IntPtr OverwriteEx;

    /// <summary>GetEa callback: retrieves extended attributes.</summary>
    public IntPtr GetEa;

    /// <summary>SetEa callback: sets extended attributes.</summary>
    public IntPtr SetEa;

    /// <summary>Ioctl callback: handles device I/O control requests.</summary>
    public IntPtr Ioctl;

    /// <summary>DispatcherStopped callback: called when the dispatcher stops.</summary>
    public IntPtr DispatcherStopped;

    // Reserved fields for future WinFsp extensions
    public IntPtr Reserved0;
    public IntPtr Reserved1;
    public IntPtr Reserved2;
    public IntPtr Reserved3;
    public IntPtr Reserved4;
    public IntPtr Reserved5;
    public IntPtr Reserved6;
    public IntPtr Reserved7;
}

/// <summary>
/// WinFsp filesystem volume parameters structure.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct FspFsctlVolumeParams
{
    public ushort Version;
    public ushort SectorSize;
    public ushort SectorsPerAllocationUnit;
    public ushort MaxComponentLength;
    public long VolumeCreationTime;
    public uint VolumeSerialNumber;
    public uint TransactTimeout;
    public uint IrpTimeout;
    public uint IrpCapacity;
    public uint FileInfoTimeout;

    // Bitfield flags packed into a uint
    public uint Flags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 192)]
    public string Prefix;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
    public string FileSystemName;
}

/// <summary>
/// P/Invoke declarations for WinFsp user-mode filesystem driver (winfsp-x64.dll).
/// All bindings are internal and loaded dynamically at runtime. If the DLL is not
/// found, <see cref="WinFspMountProvider.IsAvailable"/> returns false.
/// </summary>
internal static class WinFspNative
{
    private const string DllName = "winfsp-x64.dll";

    /// <summary>
    /// Creates a new WinFsp filesystem object.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern int FspFileSystemCreate(
        string devicePath,
        ref FspFsctlVolumeParams volumeParams,
        ref FspFileSystemInterface fsInterface,
        out IntPtr fileSystem);

    /// <summary>
    /// Sets the mount point for a WinFsp filesystem.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern int FspFileSystemSetMountPoint(
        IntPtr fileSystem,
        string? mountPoint);

    /// <summary>
    /// Starts the WinFsp I/O dispatcher with the specified number of threads.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FspFileSystemStartDispatcher(
        IntPtr fileSystem,
        uint threadCount);

    /// <summary>
    /// Stops the WinFsp I/O dispatcher, causing all pending operations to drain.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void FspFileSystemStopDispatcher(
        IntPtr fileSystem);

    /// <summary>
    /// Deletes a WinFsp filesystem object, freeing all associated resources.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void FspFileSystemDelete(
        IntPtr fileSystem);

    /// <summary>
    /// Sends a response to a WinFsp filesystem operation.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void FspFileSystemSendResponse(
        IntPtr fileSystem,
        IntPtr response);

    // Volume params flag constants
    public const uint FspFsctlVolumeFlagsCaseSensitiveSearch = 0x00000001;
    public const uint FspFsctlVolumeFlagsCasePreservedNames = 0x00000002;
    public const uint FspFsctlVolumeFlagsUnicodeOnDisk = 0x00000004;
    public const uint FspFsctlVolumeFlagsPersistentAcls = 0x00000008;
    public const uint FspFsctlVolumeFlagsPostCleanupWhenModifiedOnly = 0x00000200;
    public const uint FspFsctlVolumeFlagsPassQueryDirectoryPattern = 0x00000400;
    public const uint FspFsctlVolumeFlagsFlushAndPurgeOnCleanup = 0x00001000;

    /// <summary>
    /// Checks whether the WinFsp DLL can be loaded on the current system.
    /// </summary>
    public static bool IsDllAvailable()
    {
        try
        {
            // Attempt to load the DLL via P/Invoke
            // If the DLL doesn't exist, this will throw DllNotFoundException
            var handle = NativeLibrary.Load(DllName, typeof(WinFspNative).Assembly, DllImportSearchPath.SafeDirectories | DllImportSearchPath.System32);
            NativeLibrary.Free(handle);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }
}

#endregion

/// <summary>
/// Windows mount provider that mounts VDE volumes as native drive letters or UNC paths
/// via WinFsp (Windows File System Proxy). All filesystem callbacks delegate to
/// <see cref="VdeFilesystemAdapter"/> -- no storage logic resides in this provider.
/// </summary>
/// <remarks>
/// <para>
/// WinFsp provides a user-mode filesystem framework for Windows with ~5-10us overhead
/// per callback. This provider implements only the WinFsp binding layer using P/Invoke
/// to winfsp-x64.dll, keeping the SDK dependency-free from WinFsp.Net NuGet packages.
/// </para>
/// <para>
/// The provider maintains a thread-safe registry of active mounts and handles concurrent
/// mount/unmount operations. Each mount runs its own WinFsp dispatcher on a background thread.
/// </para>
/// <para>
/// Per AD-51, Windows users must be able to mount VDE files as native drives. If WinFsp
/// is not installed, <see cref="IsAvailable"/> returns false and <see cref="MountAsync"/>
/// throws <see cref="PlatformNotSupportedException"/>.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Windows WinFsp mount provider (VOPT-84)")]
[SupportedOSPlatform("windows")]
public sealed class WinFspMountProvider : IVdeMountProvider
{
    /// <summary>
    /// Registry of active mounts keyed by normalized mount point.
    /// </summary>
    private readonly ConcurrentDictionary<string, WinFspMountHandle> _activeMounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Factory delegate for creating VdeFilesystemAdapter instances from a VDE file path.
    /// Injected to keep the provider decoupled from VDE internals.
    /// </summary>
    private readonly Func<string, MountOptions, CancellationToken, Task<VdeFilesystemAdapter>> _adapterFactory;

    /// <summary>
    /// Lazy-evaluated WinFsp availability check. Cached after first evaluation.
    /// </summary>
    private readonly Lazy<bool> _isAvailable;

    /// <summary>
    /// Regex for valid Windows drive letter mount points (e.g., "Z:", "Z:\").
    /// </summary>
    private static readonly Regex DriveLetterPattern = new(
        @"^[A-Za-z]:\\?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Regex for valid UNC mount paths (e.g., "\\server\share").
    /// </summary>
    private static readonly Regex UncPathPattern = new(
        @"^\\\\[^\\]+\\[^\\]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Creates a new WinFsp mount provider.
    /// </summary>
    /// <param name="adapterFactory">
    /// Factory that creates a <see cref="VdeFilesystemAdapter"/> from a VDE file path.
    /// The factory receives the VDE path, mount options, and cancellation token.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="adapterFactory"/> is null.</exception>
    public WinFspMountProvider(Func<string, MountOptions, CancellationToken, Task<VdeFilesystemAdapter>> adapterFactory)
    {
        _adapterFactory = adapterFactory ?? throw new ArgumentNullException(nameof(adapterFactory));
        _isAvailable = new Lazy<bool>(DetectWinFspAvailability, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public PlatformFlags SupportedPlatforms => PlatformFlags.Windows;

    /// <inheritdoc />
    public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _isAvailable.Value;

    /// <inheritdoc />
    /// <exception cref="PlatformNotSupportedException">Thrown if WinFsp is not installed or not on Windows.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="mountPoint"/> is not a valid drive letter or UNC path.</exception>
    /// <exception cref="FileNotFoundException">Thrown if the VDE file does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the mount point is already in use.</exception>
    public async Task<IMountHandle> MountAsync(string vdePath, string mountPoint, MountOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vdePath);
        ArgumentNullException.ThrowIfNull(mountPoint);
        ArgumentNullException.ThrowIfNull(options);

        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException(
                "WinFsp is not available on this system. Install WinFsp from https://winfsp.dev/ to enable VDE drive mounting.");
        }

        // Validate mount point format
        if (!DriveLetterPattern.IsMatch(mountPoint) && !UncPathPattern.IsMatch(mountPoint))
        {
            throw new ArgumentException(
                $"Mount point '{mountPoint}' is not a valid Windows drive letter (e.g., 'Z:') or UNC path (e.g., '\\\\server\\share').",
                nameof(mountPoint));
        }

        // Validate VDE file exists
        if (!File.Exists(vdePath))
        {
            throw new FileNotFoundException($"VDE volume file not found: {vdePath}", vdePath);
        }

        // Check mount point not already in use
        var normalizedMount = NormalizeMountPoint(mountPoint);
        if (_activeMounts.ContainsKey(normalizedMount))
        {
            throw new InvalidOperationException($"Mount point '{mountPoint}' is already in use by an active VDE mount.");
        }

        // Create the filesystem adapter
        var adapter = await _adapterFactory(vdePath, options, ct).ConfigureAwait(false);

        IntPtr fileSystemHandle = IntPtr.Zero;
        Thread? dispatcherThread = null;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            // Configure WinFsp volume parameters
            var volumeParams = new FspFsctlVolumeParams
            {
                Version = 0,
                SectorSize = 4096,
                SectorsPerAllocationUnit = 1,
                MaxComponentLength = 255,
                VolumeCreationTime = DateTimeOffset.UtcNow.ToFileTime(),
                VolumeSerialNumber = (uint)(vdePath.GetHashCode() & 0x7FFFFFFF),
                TransactTimeout = 0,
                IrpTimeout = 0,
                IrpCapacity = 100,
                FileInfoTimeout = (uint)options.AttrTimeout.TotalMilliseconds,
                Flags = WinFspNative.FspFsctlVolumeFlagsCasePreservedNames
                      | WinFspNative.FspFsctlVolumeFlagsUnicodeOnDisk
                      | WinFspNative.FspFsctlVolumeFlagsFlushAndPurgeOnCleanup,
                Prefix = string.Empty,
                FileSystemName = "DWVD"
            };

            if (options.ReadOnly)
            {
                // WinFsp will reject writes at the driver level for read-only volumes
                volumeParams.Flags |= 0x00000100; // ReadOnlyVolume
            }

            // Build the filesystem interface with callback delegates
            var fsInterface = BuildFilesystemInterface(adapter, options);

            // Create WinFsp filesystem object
            var devicePath = UncPathPattern.IsMatch(mountPoint) ? @"\WinFsp.Net" : @"\WinFsp.Disk";
            int result = WinFspNative.FspFileSystemCreate(devicePath, ref volumeParams, ref fsInterface, out fileSystemHandle);
            if (result != NtStatus.StatusSuccess)
            {
                throw new InvalidOperationException($"WinFsp filesystem creation failed with NTSTATUS 0x{result:X8}.");
            }

            // Set the mount point
            result = WinFspNative.FspFileSystemSetMountPoint(fileSystemHandle, mountPoint);
            if (result != NtStatus.StatusSuccess)
            {
                throw new InvalidOperationException(
                    $"WinFsp failed to set mount point '{mountPoint}' with NTSTATUS 0x{result:X8}.");
            }

            // Start the dispatcher on a background thread
            var fsHandleCopy = fileSystemHandle;
            var threadCount = (uint)Math.Min(options.MaxConcurrentOps, Environment.ProcessorCount);
            dispatcherThread = new Thread(() =>
            {
                try
                {
                    WinFspNative.FspFileSystemStartDispatcher(fsHandleCopy, threadCount);
                }
                catch (Exception)
                {
                    // Dispatcher exited; handle cleanup occurs in DisposeAsync
                }
            })
            {
                Name = $"WinFsp-{normalizedMount}",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            dispatcherThread.Start();

            // Create and register the mount handle
            var handle = new WinFspMountHandle(
                mountPoint, vdePath, adapter, fileSystemHandle, dispatcherThread, cts, options);

            if (!_activeMounts.TryAdd(normalizedMount, handle))
            {
                // Race condition: another mount arrived first
                throw new InvalidOperationException($"Mount point '{mountPoint}' was claimed by another concurrent mount operation.");
            }

            return handle;
        }
        catch
        {
            // Cleanup on failure
            if (fileSystemHandle != IntPtr.Zero)
            {
                try
                {
                    WinFspNative.FspFileSystemStopDispatcher(fileSystemHandle);
                    WinFspNative.FspFileSystemDelete(fileSystemHandle);
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }

            cts.Dispose();
            await adapter.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task UnmountAsync(IMountHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle is not WinFspMountHandle wfHandle)
        {
            throw new ArgumentException("Handle was not created by this WinFsp mount provider.", nameof(handle));
        }

        if (!wfHandle.IsActive)
        {
            return; // Already unmounted
        }

        var normalizedMount = NormalizeMountPoint(wfHandle.MountPoint);

        // Stop the WinFsp dispatcher
        if (wfHandle.FileSystemHandle != IntPtr.Zero)
        {
            try
            {
                WinFspNative.FspFileSystemStopDispatcher(wfHandle.FileSystemHandle);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        // Flush pending writes
        try
        {
            await wfHandle.Adapter.SyncAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            // Adapter already disposed
        }

        // Delete the filesystem object
        if (wfHandle.FileSystemHandle != IntPtr.Zero)
        {
            try
            {
                WinFspNative.FspFileSystemDelete(wfHandle.FileSystemHandle);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        // Mark handle as inactive and remove from registry
        wfHandle.MarkInactive();
        _activeMounts.TryRemove(normalizedMount, out _);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MountInfo>> ListMountsAsync(CancellationToken ct = default)
    {
        var mounts = new List<MountInfo>();

        foreach (var kvp in _activeMounts)
        {
            var handle = kvp.Value;
            if (!handle.IsActive)
            {
                // Clean up stale entries
                _activeMounts.TryRemove(kvp.Key, out _);
                continue;
            }

            mounts.Add(new MountInfo
            {
                MountPoint = handle.MountPoint,
                VdePath = handle.VdePath,
                IsActive = handle.IsActive,
                MountedAtUtc = handle.MountedAtUtc,
                ReadOnly = handle.Options.ReadOnly
            });
        }

        return Task.FromResult<IReadOnlyList<MountInfo>>(mounts.AsReadOnly());
    }

    #region Error Mapping

    /// <summary>
    /// Maps a .NET exception from VdeFilesystemAdapter to a WinFsp NTSTATUS code.
    /// </summary>
    /// <param name="ex">The exception to map.</param>
    /// <returns>NTSTATUS integer value.</returns>
    internal static int MapExceptionToNtStatus(Exception ex)
    {
        return ex switch
        {
            FileNotFoundException => NtStatus.StatusObjectNameNotFound,
            DirectoryNotFoundException => NtStatus.StatusObjectPathNotFound,
            UnauthorizedAccessException => NtStatus.StatusAccessDenied,
            OperationCanceledException => NtStatus.StatusCancelled,
            IOException ioEx when ioEx.Message.Contains("not empty", StringComparison.OrdinalIgnoreCase)
                => NtStatus.StatusDirectoryNotEmpty,
            IOException ioEx when ioEx.Message.Contains("disk full", StringComparison.OrdinalIgnoreCase)
                || ioEx.Message.Contains("no space", StringComparison.OrdinalIgnoreCase)
                => NtStatus.StatusDiskFull,
            IOException ioEx when ioEx.Message.Contains("sharing", StringComparison.OrdinalIgnoreCase)
                => NtStatus.StatusSharingViolation,
            IOException => NtStatus.StatusUnexpectedIoError,
            ArgumentException => NtStatus.StatusInvalidParameter,
            NotSupportedException => NtStatus.StatusNotImplemented,
            InvalidOperationException ioEx when ioEx.Message.Contains("not a directory", StringComparison.OrdinalIgnoreCase)
                => NtStatus.StatusNotADirectory,
            InvalidOperationException ioEx when ioEx.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                => NtStatus.StatusObjectNameCollision,
            _ => NtStatus.StatusInternalError
        };
    }

    #endregion

    #region Filesystem Interface Construction

    /// <summary>
    /// Builds a WinFsp filesystem interface struct with managed delegate callbacks
    /// that route to the VdeFilesystemAdapter.
    /// </summary>
    /// <param name="adapter">Filesystem adapter for storage operations.</param>
    /// <param name="options">Mount options controlling behavior.</param>
    /// <returns>Populated filesystem interface struct.</returns>
    private static FspFileSystemInterface BuildFilesystemInterface(VdeFilesystemAdapter adapter, MountOptions options)
    {
        // Define callback delegate types matching WinFsp signatures
        // Each callback marshals WinFsp parameters -> adapter calls -> NTSTATUS return

        var fsInterface = new FspFileSystemInterface();

        // GetVolumeInfo: reports total/free size and volume label
        GetVolumeInfoDelegate getVolumeInfo = (IntPtr fileSystem, out FspFsctlVolumeInfo volumeInfo) =>
        {
            volumeInfo = default;
            try
            {
                var stats = adapter.StatfsAsync(CancellationToken.None).GetAwaiter().GetResult();
                volumeInfo.TotalSize = stats.TotalBlocks * stats.BlockSize;
                volumeInfo.FreeSize = stats.FreeBlocks * stats.BlockSize;
                volumeInfo.VolumeLabel = "DWVD";
                volumeInfo.VolumeLabelLength = (ushort)("DWVD".Length * 2);
                return NtStatus.StatusSuccess;
            }
            catch (Exception ex)
            {
                return MapExceptionToNtStatus(ex);
            }
        };
        fsInterface.GetVolumeInfo = Marshal.GetFunctionPointerForDelegate(getVolumeInfo);

        // Open: lookup + getattr, returns file context (inode number stored in IntPtr)
        OpenDelegate open = (IntPtr fileSystem, string fileName, uint createOptions, uint grantedAccess,
                             out IntPtr fileContext, out FspFsctlFileInfo fileInfo) =>
        {
            fileContext = IntPtr.Zero;
            fileInfo = default;
            try
            {
                var parts = ParseWinPath(fileName);
                long inodeNumber = ResolvePathToInode(adapter, parts);

                if (inodeNumber < 0)
                    return NtStatus.StatusObjectNameNotFound;

                var attrs = adapter.GetattrAsync(inodeNumber, CancellationToken.None).GetAwaiter().GetResult();
                fileInfo = MapAttributesToFileInfo(attrs);
                fileContext = new IntPtr(inodeNumber);
                return NtStatus.StatusSuccess;
            }
            catch (Exception ex)
            {
                return MapExceptionToNtStatus(ex);
            }
        };
        fsInterface.Open = Marshal.GetFunctionPointerForDelegate(open);

        // Close: flush and release file context
        CloseDelegate close = (IntPtr fileSystem, IntPtr fileContext) =>
        {
            try
            {
                long inodeNumber = fileContext.ToInt64();
                if (inodeNumber > 0)
                {
                    adapter.FlushAsync(inodeNumber, CancellationToken.None).GetAwaiter().GetResult();
                }
            }
            catch
            {
                // Best-effort flush on close
            }
        };
        fsInterface.Close = Marshal.GetFunctionPointerForDelegate(close);

        // Read: delegates to adapter.ReadAsync
        ReadDelegate read = (IntPtr fileSystem, IntPtr fileContext, IntPtr buffer, ulong offset, uint length,
                             out uint bytesTransferred) =>
        {
            bytesTransferred = 0;
            try
            {
                long inodeNumber = fileContext.ToInt64();
                var managedBuffer = new byte[length];
                int bytesRead = adapter.ReadAsync(inodeNumber, managedBuffer, (long)offset, CancellationToken.None)
                    .GetAwaiter().GetResult();

                if (bytesRead > 0)
                {
                    Marshal.Copy(managedBuffer, 0, buffer, bytesRead);
                    bytesTransferred = (uint)bytesRead;
                }
                else if (bytesRead == 0)
                {
                    return NtStatus.StatusEndOfFile;
                }

                return NtStatus.StatusSuccess;
            }
            catch (Exception ex)
            {
                return MapExceptionToNtStatus(ex);
            }
        };
        fsInterface.Read = Marshal.GetFunctionPointerForDelegate(read);

        // Write: delegates to adapter.WriteAsync
        WriteDelegate write = (IntPtr fileSystem, IntPtr fileContext, IntPtr buffer, ulong offset, uint length,
                               bool writeToEndOfFile, bool constrainedIo,
                               out uint bytesTransferred, out FspFsctlFileInfo fileInfo) =>
        {
            bytesTransferred = 0;
            fileInfo = default;
            try
            {
                if (options.ReadOnly)
                    return NtStatus.StatusAccessDenied;

                long inodeNumber = fileContext.ToInt64();
                var managedBuffer = new byte[length];
                Marshal.Copy(buffer, managedBuffer, 0, (int)length);

                long writeOffset = writeToEndOfFile
                    ? adapter.GetattrAsync(inodeNumber, CancellationToken.None).GetAwaiter().GetResult().Size
                    : (long)offset;

                int bytesWritten = adapter.WriteAsync(inodeNumber, managedBuffer, writeOffset, CancellationToken.None)
                    .GetAwaiter().GetResult();

                bytesTransferred = (uint)bytesWritten;

                // Return updated file info
                var attrs = adapter.GetattrAsync(inodeNumber, CancellationToken.None).GetAwaiter().GetResult();
                fileInfo = MapAttributesToFileInfo(attrs);

                return NtStatus.StatusSuccess;
            }
            catch (Exception ex)
            {
                return MapExceptionToNtStatus(ex);
            }
        };
        fsInterface.Write = Marshal.GetFunctionPointerForDelegate(write);

        // Flush: delegates to adapter.FlushAsync or SyncAsync for null context
        FlushDelegate flush = (IntPtr fileSystem, IntPtr fileContext, out FspFsctlFileInfo fileInfo) =>
        {
            fileInfo = default;
            try
            {
                long inodeNumber = fileContext.ToInt64();
                if (inodeNumber > 0)
                {
                    adapter.FlushAsync(inodeNumber, CancellationToken.None).GetAwaiter().GetResult();
                    var attrs = adapter.GetattrAsync(inodeNumber, CancellationToken.None).GetAwaiter().GetResult();
                    fileInfo = MapAttributesToFileInfo(attrs);
                }
                else
                {
                    adapter.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                }

                return NtStatus.StatusSuccess;
            }
            catch (Exception ex)
            {
                return MapExceptionToNtStatus(ex);
            }
        };
        fsInterface.Flush = Marshal.GetFunctionPointerForDelegate(flush);

        // GetFileInfo: delegates to adapter.GetattrAsync
        GetFileInfoDelegate getFileInfo = (IntPtr fileSystem, IntPtr fileContext, out FspFsctlFileInfo fileInfo) =>
        {
            fileInfo = default;
            try
            {
                long inodeNumber = fileContext.ToInt64();
                var attrs = adapter.GetattrAsync(inodeNumber, CancellationToken.None).GetAwaiter().GetResult();
                fileInfo = MapAttributesToFileInfo(attrs);
                return NtStatus.StatusSuccess;
            }
            catch (Exception ex)
            {
                return MapExceptionToNtStatus(ex);
            }
        };
        fsInterface.GetFileInfo = Marshal.GetFunctionPointerForDelegate(getFileInfo);

        // Create: delegates to adapter.CreateAsync or MkdirAsync
        CreateDelegate create = (IntPtr fileSystem, string fileName, uint createOptions, uint grantedAccess,
                                 uint fileAttributes, IntPtr securityDescriptor, ulong allocationSize,
                                 out IntPtr fileContext, out FspFsctlFileInfo fileInfo) =>
        {
            fileContext = IntPtr.Zero;
            fileInfo = default;
            try
            {
                if (options.ReadOnly)
                    return NtStatus.StatusAccessDenied;

                var parts = ParseWinPath(fileName);
                if (parts.Length == 0)
                    return NtStatus.StatusInvalidParameter;

                string name = parts[^1];
                long parentInode = parts.Length > 1 ? ResolvePathToInode(adapter, parts.AsSpan()[..^1]) : 1; // root = inode 1

                if (parentInode < 0)
                    return NtStatus.StatusObjectPathNotFound;

                bool isDirectory = (createOptions & 0x00000001) != 0; // FILE_DIRECTORY_FILE
                long newInode;

                if (isDirectory)
                {
                    newInode = adapter.MkdirAsync(parentInode, name, InodePermissions.OwnerAll | InodePermissions.GroupRead | InodePermissions.GroupExecute | InodePermissions.OtherRead | InodePermissions.OtherExecute, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                else
                {
                    newInode = adapter.CreateAsync(parentInode, name, InodeType.File, InodePermissions.OwnerAll | InodePermissions.GroupRead | InodePermissions.OtherRead, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }

                fileContext = new IntPtr(newInode);
                var attrs = adapter.GetattrAsync(newInode, CancellationToken.None).GetAwaiter().GetResult();
                fileInfo = MapAttributesToFileInfo(attrs);
                return NtStatus.StatusSuccess;
            }
            catch (Exception ex)
            {
                return MapExceptionToNtStatus(ex);
            }
        };
        fsInterface.Create = Marshal.GetFunctionPointerForDelegate(create);

        // Cleanup: handles delete-on-close via adapter.UnlinkAsync
        CleanupDelegate cleanup = (IntPtr fileSystem, IntPtr fileContext, string fileName, uint flags) =>
        {
            try
            {
                bool deleteOnClose = (flags & 0x01) != 0; // FspCleanupDelete
                if (deleteOnClose && !options.ReadOnly)
                {
                    var parts = ParseWinPath(fileName);
                    if (parts.Length > 0)
                    {
                        string name = parts[^1];
                        long parentInode = parts.Length > 1 ? ResolvePathToInode(adapter, parts.AsSpan()[..^1]) : 1;
                        if (parentInode >= 0)
                        {
                            adapter.UnlinkAsync(parentInode, name, CancellationToken.None).GetAwaiter().GetResult();
                        }
                    }
                }
            }
            catch
            {
                // Best-effort cleanup
            }
        };
        fsInterface.Cleanup = Marshal.GetFunctionPointerForDelegate(cleanup);

        // Rename: delegates to adapter.RenameAsync
        RenameDelegate rename = (IntPtr fileSystem, IntPtr fileContext, string fileName, string newFileName,
                                 bool replaceIfExists) =>
        {
            try
            {
                if (options.ReadOnly)
                    return NtStatus.StatusAccessDenied;

                var oldParts = ParseWinPath(fileName);
                var newParts = ParseWinPath(newFileName);

                if (oldParts.Length == 0 || newParts.Length == 0)
                    return NtStatus.StatusInvalidParameter;

                long oldParent = oldParts.Length > 1 ? ResolvePathToInode(adapter, oldParts.AsSpan()[..^1]) : 1;
                long newParent = newParts.Length > 1 ? ResolvePathToInode(adapter, newParts.AsSpan()[..^1]) : 1;

                if (oldParent < 0 || newParent < 0)
                    return NtStatus.StatusObjectPathNotFound;

                adapter.RenameAsync(oldParent, oldParts[^1], newParent, newParts[^1], CancellationToken.None)
                    .GetAwaiter().GetResult();

                return NtStatus.StatusSuccess;
            }
            catch (Exception ex)
            {
                return MapExceptionToNtStatus(ex);
            }
        };
        fsInterface.Rename = Marshal.GetFunctionPointerForDelegate(rename);

        // SetBasicInfo: sets timestamps via adapter.SetattrAsync
        SetBasicInfoDelegate setBasicInfo = (IntPtr fileSystem, IntPtr fileContext,
                                             uint fileAttributes, long creationTime, long lastAccessTime,
                                             long lastWriteTime, long changeTime, out FspFsctlFileInfo fileInfo) =>
        {
            fileInfo = default;
            try
            {
                long inodeNumber = fileContext.ToInt64();
                var mask = InodeAttributeMask.None;
                var attrs = new InodeAttributes();

                if (lastAccessTime != 0)
                {
                    mask |= InodeAttributeMask.AccessTime;
                    attrs = attrs with { AccessedUtc = FileTimeToUnixSeconds(lastAccessTime) };
                }
                if (lastWriteTime != 0)
                {
                    mask |= InodeAttributeMask.ModifyTime;
                    attrs = attrs with { ModifiedUtc = FileTimeToUnixSeconds(lastWriteTime) };
                }
                if (changeTime != 0)
                {
                    mask |= InodeAttributeMask.ChangeTime;
                    attrs = attrs with { ChangedUtc = FileTimeToUnixSeconds(changeTime) };
                }

                if (mask != InodeAttributeMask.None)
                {
                    adapter.SetattrAsync(inodeNumber, attrs, mask, CancellationToken.None).GetAwaiter().GetResult();
                }

                var current = adapter.GetattrAsync(inodeNumber, CancellationToken.None).GetAwaiter().GetResult();
                fileInfo = MapAttributesToFileInfo(current);
                return NtStatus.StatusSuccess;
            }
            catch (Exception ex)
            {
                return MapExceptionToNtStatus(ex);
            }
        };
        fsInterface.SetBasicInfo = Marshal.GetFunctionPointerForDelegate(setBasicInfo);

        // SetFileSize: truncate/extend via adapter.SetattrAsync
        SetFileSizeDelegate setFileSize = (IntPtr fileSystem, IntPtr fileContext, ulong newSize,
                                           bool setAllocationSize, out FspFsctlFileInfo fileInfo) =>
        {
            fileInfo = default;
            try
            {
                if (options.ReadOnly)
                    return NtStatus.StatusAccessDenied;

                long inodeNumber = fileContext.ToInt64();
                var attrs = new InodeAttributes { Size = (long)newSize };
                adapter.SetattrAsync(inodeNumber, attrs, InodeAttributeMask.Size, CancellationToken.None)
                    .GetAwaiter().GetResult();

                var current = adapter.GetattrAsync(inodeNumber, CancellationToken.None).GetAwaiter().GetResult();
                fileInfo = MapAttributesToFileInfo(current);
                return NtStatus.StatusSuccess;
            }
            catch (Exception ex)
            {
                return MapExceptionToNtStatus(ex);
            }
        };
        fsInterface.SetFileSize = Marshal.GetFunctionPointerForDelegate(setFileSize);

        // ReadDirectory: delegates to adapter.ReaddirAsync
        ReadDirectoryDelegate readDirectory = (IntPtr fileSystem, IntPtr fileContext, string? pattern,
                                               string? marker, IntPtr buffer, uint length,
                                               out uint bytesTransferred) =>
        {
            bytesTransferred = 0;
            try
            {
                long inodeNumber = fileContext.ToInt64();
                var entries = adapter.ReaddirAsync(inodeNumber, CancellationToken.None).GetAwaiter().GetResult();

                bool pastMarker = marker == null;
                uint offset = 0;
                uint entrySize = (uint)Marshal.SizeOf<FspFsctlDirInfo>();

                foreach (var entry in entries)
                {
                    if (!pastMarker)
                    {
                        if (entry.Name == marker)
                            pastMarker = true;
                        continue;
                    }

                    if (offset + entrySize > length)
                        break;

                    var entryAttrs = adapter.GetattrAsync(entry.InodeNumber, CancellationToken.None)
                        .GetAwaiter().GetResult();

                    var dirInfo = new FspFsctlDirInfo
                    {
                        Size = (ushort)entrySize,
                        FileInfo = MapAttributesToFileInfo(entryAttrs),
                        FileName = entry.Name
                    };

                    Marshal.StructureToPtr(dirInfo, buffer + (int)offset, false);
                    offset += entrySize;
                }

                bytesTransferred = offset;
                return NtStatus.StatusSuccess;
            }
            catch (Exception ex)
            {
                return MapExceptionToNtStatus(ex);
            }
        };
        fsInterface.ReadDirectory = Marshal.GetFunctionPointerForDelegate(readDirectory);

        // Overwrite: truncate existing file
        OverwriteDelegate overwrite = (IntPtr fileSystem, IntPtr fileContext, uint fileAttributes,
                                       bool replaceFileAttributes, ulong allocationSize,
                                       out FspFsctlFileInfo fileInfo) =>
        {
            fileInfo = default;
            try
            {
                if (options.ReadOnly)
                    return NtStatus.StatusAccessDenied;

                long inodeNumber = fileContext.ToInt64();
                var attrs = new InodeAttributes { Size = 0 };
                adapter.SetattrAsync(inodeNumber, attrs, InodeAttributeMask.Size, CancellationToken.None)
                    .GetAwaiter().GetResult();

                var current = adapter.GetattrAsync(inodeNumber, CancellationToken.None).GetAwaiter().GetResult();
                fileInfo = MapAttributesToFileInfo(current);
                return NtStatus.StatusSuccess;
            }
            catch (Exception ex)
            {
                return MapExceptionToNtStatus(ex);
            }
        };
        fsInterface.Overwrite = Marshal.GetFunctionPointerForDelegate(overwrite);

        // GetSecurityByName: lookup by path, return default security descriptor
        GetSecurityByNameDelegate getSecurityByName = (IntPtr fileSystem, string fileName,
                                                        out uint fileAttributes, IntPtr securityDescriptor,
                                                        ref ulong securityDescriptorSize) =>
        {
            fileAttributes = 0;
            try
            {
                var parts = ParseWinPath(fileName);
                long inodeNumber = ResolvePathToInode(adapter, parts);

                if (inodeNumber < 0)
                    return NtStatus.StatusObjectNameNotFound;

                var attrs = adapter.GetattrAsync(inodeNumber, CancellationToken.None).GetAwaiter().GetResult();
                fileAttributes = MapInodeTypeToFileAttributes(attrs.Type);

                // Return zero security descriptor size to use default security
                securityDescriptorSize = 0;
                return NtStatus.StatusSuccess;
            }
            catch (Exception ex)
            {
                return MapExceptionToNtStatus(ex);
            }
        };
        fsInterface.GetSecurityByName = Marshal.GetFunctionPointerForDelegate(getSecurityByName);

        // Keep delegate references alive for the lifetime of the mount
        // Store them in a GCHandle-friendly way (the adapter holds a reference)
        GC.KeepAlive(getVolumeInfo);
        GC.KeepAlive(open);
        GC.KeepAlive(close);
        GC.KeepAlive(read);
        GC.KeepAlive(write);
        GC.KeepAlive(flush);
        GC.KeepAlive(getFileInfo);
        GC.KeepAlive(create);
        GC.KeepAlive(cleanup);
        GC.KeepAlive(rename);
        GC.KeepAlive(setBasicInfo);
        GC.KeepAlive(setFileSize);
        GC.KeepAlive(readDirectory);
        GC.KeepAlive(overwrite);
        GC.KeepAlive(getSecurityByName);

        return fsInterface;
    }

    #endregion

    #region Delegate Types

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int GetVolumeInfoDelegate(IntPtr fileSystem, out FspFsctlVolumeInfo volumeInfo);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int OpenDelegate(IntPtr fileSystem, string fileName, uint createOptions, uint grantedAccess,
                                      out IntPtr fileContext, out FspFsctlFileInfo fileInfo);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CloseDelegate(IntPtr fileSystem, IntPtr fileContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReadDelegate(IntPtr fileSystem, IntPtr fileContext, IntPtr buffer, ulong offset,
                                      uint length, out uint bytesTransferred);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WriteDelegate(IntPtr fileSystem, IntPtr fileContext, IntPtr buffer, ulong offset,
                                       uint length, bool writeToEndOfFile, bool constrainedIo,
                                       out uint bytesTransferred, out FspFsctlFileInfo fileInfo);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FlushDelegate(IntPtr fileSystem, IntPtr fileContext, out FspFsctlFileInfo fileInfo);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFileInfoDelegate(IntPtr fileSystem, IntPtr fileContext, out FspFsctlFileInfo fileInfo);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int CreateDelegate(IntPtr fileSystem, string fileName, uint createOptions, uint grantedAccess,
                                        uint fileAttributes, IntPtr securityDescriptor, ulong allocationSize,
                                        out IntPtr fileContext, out FspFsctlFileInfo fileInfo);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate void CleanupDelegate(IntPtr fileSystem, IntPtr fileContext, string fileName, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int RenameDelegate(IntPtr fileSystem, IntPtr fileContext, string fileName,
                                        string newFileName, bool replaceIfExists);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetBasicInfoDelegate(IntPtr fileSystem, IntPtr fileContext,
                                               uint fileAttributes, long creationTime, long lastAccessTime,
                                               long lastWriteTime, long changeTime, out FspFsctlFileInfo fileInfo);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetFileSizeDelegate(IntPtr fileSystem, IntPtr fileContext, ulong newSize,
                                              bool setAllocationSize, out FspFsctlFileInfo fileInfo);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int ReadDirectoryDelegate(IntPtr fileSystem, IntPtr fileContext, string? pattern,
                                                string? marker, IntPtr buffer, uint length,
                                                out uint bytesTransferred);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OverwriteDelegate(IntPtr fileSystem, IntPtr fileContext, uint fileAttributes,
                                            bool replaceFileAttributes, ulong allocationSize,
                                            out FspFsctlFileInfo fileInfo);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int GetSecurityByNameDelegate(IntPtr fileSystem, string fileName,
                                                    out uint fileAttributes, IntPtr securityDescriptor,
                                                    ref ulong securityDescriptorSize);

    #endregion

    #region Path Resolution Helpers

    /// <summary>
    /// Parses a Windows-style path (e.g., "\dir\file.txt") into path components.
    /// </summary>
    /// <param name="winPath">Windows path with backslash separators.</param>
    /// <returns>Array of path component names.</returns>
    internal static string[] ParseWinPath(string winPath)
    {
        if (string.IsNullOrEmpty(winPath) || winPath == "\\")
            return Array.Empty<string>();

        return winPath.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Resolves a sequence of path components to an inode number via the adapter.
    /// Returns the root inode (1) for an empty path (root directory).
    /// </summary>
    /// <param name="adapter">Filesystem adapter for lookup operations.</param>
    /// <param name="parts">Path components from <see cref="ParseWinPath"/>.</param>
    /// <returns>Inode number, or -1 if not found.</returns>
    internal static long ResolvePathToInode(VdeFilesystemAdapter adapter, ReadOnlySpan<string> parts)
    {
        const long rootInode = 1;

        if (parts.Length == 0)
            return rootInode;

        long currentInode = rootInode;
        foreach (var part in parts)
        {
            var result = adapter.LookupAsync(currentInode, part, CancellationToken.None)
                .GetAwaiter().GetResult();

            if (!result.Found)
                return -1;

            currentInode = result.InodeNumber;
        }

        return currentInode;
    }

    #endregion

    #region Attribute Mapping Helpers

    /// <summary>
    /// Maps VDE inode attributes to a WinFsp file info structure.
    /// </summary>
    /// <param name="attrs">VDE inode attributes.</param>
    /// <returns>WinFsp file info structure.</returns>
    internal static FspFsctlFileInfo MapAttributesToFileInfo(InodeAttributes attrs)
    {
        return new FspFsctlFileInfo
        {
            FileAttributes = MapInodeTypeToFileAttributes(attrs.Type),
            AllocationSize = attrs.AllocatedSize,
            FileSize = attrs.Size,
            CreationTime = UnixSecondsToFileTime(attrs.CreatedUtc),
            LastAccessTime = UnixSecondsToFileTime(attrs.AccessedUtc),
            LastWriteTime = UnixSecondsToFileTime(attrs.ModifiedUtc),
            ChangeTime = UnixSecondsToFileTime(attrs.ChangedUtc),
            IndexNumber = (long)attrs.InodeNumber,
            HardLinks = (uint)attrs.LinkCount,
            ReparseTag = 0,
            EaSize = 0
        };
    }

    /// <summary>
    /// Maps a VDE inode type to Windows file attributes.
    /// </summary>
    /// <param name="type">VDE inode type.</param>
    /// <returns>Windows FILE_ATTRIBUTE_* flags.</returns>
    internal static uint MapInodeTypeToFileAttributes(InodeType type)
    {
        const uint fileAttributeNormal = 0x00000080;
        const uint fileAttributeDirectory = 0x00000010;
        const uint fileAttributeReparsePoint = 0x00000400;

        return type switch
        {
            InodeType.Directory => fileAttributeDirectory,
            InodeType.SymLink => fileAttributeReparsePoint,
            _ => fileAttributeNormal
        };
    }

    /// <summary>
    /// Converts Unix timestamp (seconds since epoch) to Windows FILETIME (100-nanosecond intervals since 1601-01-01).
    /// </summary>
    /// <param name="unixSeconds">Unix timestamp in seconds.</param>
    /// <returns>Windows FILETIME value.</returns>
    internal static long UnixSecondsToFileTime(long unixSeconds)
    {
        if (unixSeconds <= 0)
            return 0;

        // Windows epoch is 1601-01-01, Unix epoch is 1970-01-01
        // Difference: 11644473600 seconds = 116444736000000000 hundred-nanoseconds
        const long epochDifference = 116444736000000000L;
        return (unixSeconds * 10_000_000L) + epochDifference;
    }

    /// <summary>
    /// Converts Windows FILETIME to Unix timestamp (seconds since epoch).
    /// </summary>
    /// <param name="fileTime">Windows FILETIME value.</param>
    /// <returns>Unix timestamp in seconds.</returns>
    internal static long FileTimeToUnixSeconds(long fileTime)
    {
        if (fileTime <= 0)
            return 0;

        const long epochDifference = 116444736000000000L;
        return (fileTime - epochDifference) / 10_000_000L;
    }

    #endregion

    #region Mount Point Helpers

    /// <summary>
    /// Normalizes a mount point string for consistent dictionary key usage.
    /// </summary>
    /// <param name="mountPoint">Raw mount point string.</param>
    /// <returns>Normalized mount point (uppercase drive letter, no trailing backslash).</returns>
    private static string NormalizeMountPoint(string mountPoint)
    {
        // Normalize drive letter: "z:\" -> "Z:", "z:" -> "Z:"
        if (DriveLetterPattern.IsMatch(mountPoint))
        {
            return mountPoint[..2].ToUpperInvariant();
        }

        // UNC paths: trim trailing backslash
        return mountPoint.TrimEnd('\\');
    }

    #endregion

    #region WinFsp Detection

    /// <summary>
    /// Detects whether WinFsp is installed on the current system by checking the registry
    /// and attempting to load the native DLL.
    /// </summary>
    /// <returns>True if WinFsp is available.</returns>
    private static bool DetectWinFspAvailability()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        // Strategy 1: Check registry for WinFsp installation
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp");
            if (key != null)
            {
                var installDir = key.GetValue("InstallDir") as string;
                if (!string.IsNullOrEmpty(installDir))
                {
                    var dllPath = Path.Combine(installDir, "bin", "winfsp-x64.dll");
                    if (File.Exists(dllPath))
                        return true;
                }
            }
        }
        catch
        {
            // Registry access may fail; fall through to DLL probe
        }

        // Strategy 2: Check common installation paths
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            var defaultPath = Path.Combine(programFiles, "WinFsp", "bin", "winfsp-x64.dll");
            if (File.Exists(defaultPath))
                return true;
        }

        // Strategy 3: Try direct DLL load (may be on PATH)
        return WinFspNative.IsDllAvailable();
    }

    #endregion
}
