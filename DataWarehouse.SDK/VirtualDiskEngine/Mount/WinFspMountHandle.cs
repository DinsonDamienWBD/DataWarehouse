using DataWarehouse.SDK.Contracts;
using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mount;

/// <summary>
/// Active mount handle for a VDE volume mounted as a Windows drive letter via WinFsp.
/// Disposing the handle stops the WinFsp dispatcher, flushes pending writes, and releases
/// the native filesystem object.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Windows WinFsp mount handle (VOPT-84)")]
[SupportedOSPlatform("windows")]
public sealed class WinFspMountHandle : IMountHandle
{
    private readonly VdeFilesystemAdapter _adapter;
    private readonly IntPtr _fileSystemHandle;
    private readonly Thread? _mountThread;
    private readonly CancellationTokenSource _cts;
    private volatile bool _isActive;
    private int _disposed;

    /// <summary>
    /// Creates a new WinFsp mount handle.
    /// </summary>
    /// <param name="mountPoint">Drive letter or UNC path where the volume is mounted.</param>
    /// <param name="vdePath">Path to the VDE volume file.</param>
    /// <param name="adapter">Filesystem adapter for storage operations.</param>
    /// <param name="fileSystemHandle">Native WinFsp filesystem object handle.</param>
    /// <param name="mountThread">Background thread running the WinFsp dispatcher.</param>
    /// <param name="cts">Cancellation token source for signaling shutdown.</param>
    /// <param name="options">Mount options used for this session.</param>
    internal WinFspMountHandle(
        string mountPoint,
        string vdePath,
        VdeFilesystemAdapter adapter,
        IntPtr fileSystemHandle,
        Thread? mountThread,
        CancellationTokenSource cts,
        MountOptions options)
    {
        MountPoint = mountPoint ?? throw new ArgumentNullException(nameof(mountPoint));
        VdePath = vdePath ?? throw new ArgumentNullException(nameof(vdePath));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _fileSystemHandle = fileSystemHandle;
        _mountThread = mountThread;
        _cts = cts ?? throw new ArgumentNullException(nameof(cts));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        MountedAtUtc = DateTimeOffset.UtcNow;
        _isActive = true;
    }

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

    /// <summary>
    /// Gets the native WinFsp filesystem object handle for provider-level management.
    /// </summary>
    internal IntPtr FileSystemHandle => _fileSystemHandle;

    /// <summary>
    /// Gets the filesystem adapter for flush/sync operations during unmount.
    /// </summary>
    internal VdeFilesystemAdapter Adapter => _adapter;

    /// <summary>
    /// Marks this handle as inactive. Called by the provider during explicit unmount.
    /// </summary>
    internal void MarkInactive()
    {
        _isActive = false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        _isActive = false;

        // Signal the dispatcher thread to stop
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // CTS already disposed
        }

        // Wait for the mount thread to exit gracefully
        if (_mountThread is { IsAlive: true })
        {
            // Give the dispatcher up to 5 seconds to shut down
            var deadline = Environment.TickCount64 + 5000;
            while (_mountThread.IsAlive && Environment.TickCount64 < deadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        // Flush all pending writes through the adapter
        try
        {
            using var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _adapter.SyncAsync(flushCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Best-effort flush on dispose
        }
        catch (ObjectDisposedException)
        {
            // Adapter already disposed
        }

        // Stop the WinFsp dispatcher and delete the filesystem object
        if (_fileSystemHandle != IntPtr.Zero)
        {
            try
            {
                WinFspNative.FspFileSystemStopDispatcher(_fileSystemHandle);
                WinFspNative.FspFileSystemDelete(_fileSystemHandle);
            }
            catch (DllNotFoundException)
            {
                // WinFsp DLL unloaded; nothing we can do
            }
            catch (EntryPointNotFoundException)
            {
                // Incompatible WinFsp version
            }
        }

        _cts.Dispose();
    }
}
