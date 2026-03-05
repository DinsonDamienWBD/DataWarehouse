using DataWarehouse.SDK.Contracts;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mount;

/// <summary>
/// Active mount handle for a macOS macFUSE mount session.
/// Manages the fuse_session lifecycle, background event loop thread,
/// and clean teardown including unmount and adapter sync.
/// </summary>
/// <remarks>
/// <para>
/// Disposing the handle triggers <c>fuse_session_exit</c> to break the event loop,
/// followed by unmount, session destruction, and adapter sync. The handle ensures
/// all GC-pinned resources (callback delegates, GCHandle for userdata) are freed.
/// </para>
/// <para>
/// This handle is macOS-specific and works with both macFUSE kext and fuse-t
/// user-space implementations via <see cref="MacFuseNative"/>.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: macOS macFUSE mount handle (VOPT-86)")]
[SupportedOSPlatform("osx")]
public sealed class MacFuseMountHandle : IMountHandle
{
    private readonly IntPtr _fuseSession;
    private readonly VdeFilesystemAdapter _adapter;
    private readonly Thread? _loopThread;
    private readonly GCHandle _userDataHandle;
    private readonly GCHandle[] _delegateHandles;
    private readonly CancellationTokenSource _cts;
    private volatile bool _isActive;
    private bool _disposed;

    /// <inheritdoc />
    public string MountPoint { get; }

    /// <inheritdoc />
    public string VdePath { get; }

    /// <inheritdoc />
    public bool IsActive => _isActive;

    /// <inheritdoc />
    public DateTimeOffset MountedAtUtc { get; }

    /// <inheritdoc />
    public MountOptions Options { get; }

    internal MacFuseMountHandle(
        string mountPoint,
        string vdePath,
        IntPtr fuseSession,
        VdeFilesystemAdapter adapter,
        Thread? loopThread,
        GCHandle userDataHandle,
        GCHandle[] delegateHandles,
        MountOptions options)
    {
        MountPoint = mountPoint ?? throw new ArgumentNullException(nameof(mountPoint));
        VdePath = vdePath ?? throw new ArgumentNullException(nameof(vdePath));
        _fuseSession = fuseSession;
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _loopThread = loopThread;
        _userDataHandle = userDataHandle;
        _delegateHandles = delegateHandles ?? throw new ArgumentNullException(nameof(delegateHandles));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _cts = new CancellationTokenSource();
        _isActive = true;
        MountedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Creates a <see cref="MountInfo"/> snapshot of this handle's current state.
    /// </summary>
    internal MountInfo ToMountInfo()
    {
        return new MountInfo
        {
            MountPoint = MountPoint,
            VdePath = VdePath,
            IsActive = IsActive,
            MountedAtUtc = MountedAtUtc,
            ReadOnly = Options.ReadOnly
        };
    }

    /// <summary>
    /// Performs a clean unmount: signals the FUSE event loop to exit, joins the
    /// background thread, unmounts the filesystem, destroys the session, syncs
    /// the adapter, and frees all pinned resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _isActive = false;

        try
        {
            // Signal the event loop to exit
            if (_fuseSession != IntPtr.Zero)
            {
                MacFuseNative.fuse_session_exit(_fuseSession);
            }

            // Wait for the loop thread to finish
            if (_loopThread is { IsAlive: true })
            {
                _loopThread.Join(TimeSpan.FromSeconds(10));
            }

            // Unmount and destroy the session
            if (_fuseSession != IntPtr.Zero)
            {
                MacFuseNative.fuse_session_unmount(_fuseSession);
                MacFuseNative.fuse_session_destroy(_fuseSession);
            }

            // Sync adapter to flush any pending writes
            await _adapter.SyncAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // Free the GCHandle for userdata
            if (_userDataHandle.IsAllocated)
            {
                _userDataHandle.Free();
            }

            // Free pinned delegate handles
            foreach (var handle in _delegateHandles)
            {
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
            }

            _cts.Dispose();
        }
    }
}
