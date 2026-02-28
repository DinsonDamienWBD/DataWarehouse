using DataWarehouse.SDK.Contracts;
using OpenCvSharp;

namespace DataWarehouse.SDK.Edge.Camera;

/// <summary>
/// Camera frame grabber using OpenCvSharp4 for cross-platform camera access.
/// </summary>
/// <remarks>
/// <para>
/// Wraps OpenCV VideoCapture for USB, CSI, and IP camera support. Provides cross-platform
/// compatibility (Linux, Windows, macOS) without platform-specific P/Invoke.
/// </para>
/// <para>
/// <strong>Platform Notes:</strong>
/// <list type="bullet">
///   <item><description>Linux: Accesses cameras via V4L2 (/dev/videoX)</description></item>
///   <item><description>Windows: Accesses cameras via DirectShow (device index)</description></item>
///   <item><description>macOS: Accesses cameras via AVFoundation</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Performance:</strong> Frame capture involves memory copy from OpenCV Mat to managed array.
/// For zero-copy, consider platform-specific implementations (V4L2 mmap on Linux, DirectShow buffers on Windows).
/// </para>
/// <para>
/// <strong>Limitations:</strong> DevicePath must be parseable as integer (device index). For advanced
/// device selection (by name, USB ID), extend with OpenCV device enumeration APIs.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Camera frame grabber (EDGE-07)")]
public sealed class CameraFrameGrabber : ICameraDevice
{
    private VideoCapture? _capture;
    private CameraSettings? _currentSettings;
    private Mat? _frameMat;

    /// <summary>
    /// Gets whether the camera is currently open.
    /// </summary>
    public bool IsOpen => _capture?.IsOpened() ?? false;

    /// <summary>
    /// Gets the current camera settings.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if camera is not open.</exception>
    public CameraSettings CurrentSettings => _currentSettings ?? throw new InvalidOperationException("Camera not open");

    /// <summary>
    /// Opens the camera device with specified settings.
    /// </summary>
    /// <param name="settings">Camera configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if device cannot be opened.</exception>
    public Task OpenAsync(CameraSettings settings, CancellationToken ct = default)
    {
        // Offload blocking OpenCV constructor and ioctl calls to the thread pool to
        // avoid starvation on constrained single-threaded schedulers (finding P1-274).
        return Task.Run(() =>
        {
            // Parse device path as integer index (0, 1, 2, etc.) or use 0 as default
            var deviceIndex = int.TryParse(settings.DevicePath, out var idx) ? idx : 0;

            var capture = new VideoCapture(deviceIndex);
            if (!capture.IsOpened())
            {
                capture.Dispose();
                throw new InvalidOperationException($"Failed to open camera device {deviceIndex}");
            }

            // Configure capture properties
            capture.Set(VideoCaptureProperties.FrameWidth, settings.Width);
            capture.Set(VideoCaptureProperties.FrameHeight, settings.Height);
            capture.Set(VideoCaptureProperties.Fps, settings.FrameRate);

            _capture = capture;
            _currentSettings = settings;
            _frameMat = new Mat();
        }, ct);
    }

    /// <summary>
    /// Closes the camera device and releases resources.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public Task CloseAsync(CancellationToken ct = default)
    {
        // Offload blocking OpenCV release to thread pool (finding P1-274)
        return Task.Run(() =>
        {
            _capture?.Release();
            _capture?.Dispose();
            _frameMat?.Dispose();
            _capture = null;
            _frameMat = null;
            _currentSettings = null;
        }, ct);
    }

    /// <summary>
    /// Captures a single frame from the camera.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>FrameBuffer containing captured frame; null if no frame available.</returns>
    /// <exception cref="InvalidOperationException">Thrown if camera is not open.</exception>
    public Task<FrameBuffer?> CaptureFrameAsync(CancellationToken ct = default)
    {
        if (_capture is null || _frameMat is null)
            throw new InvalidOperationException("Camera not open");

        // Offload blocking VideoCapture.Read (V4L2/DirectShow/AVFoundation ioctl) to thread pool
        // to honour async contract and prevent thread-pool starvation on constrained devices (finding P1-274).
        var capture = _capture;
        var frameMat = _frameMat;
        var settings = _currentSettings!;
        return Task.Run<FrameBuffer?>(() =>
        {
            var success = capture.Read(frameMat);
            if (!success || frameMat.Empty())
                return null;

            // Convert Mat to byte array (requires copy for managed access)
            var dataSize = (int)(frameMat.Total() * frameMat.ElemSize());
            var data = new byte[dataSize];
            System.Runtime.InteropServices.Marshal.Copy(frameMat.Data, data, 0, dataSize);

            return new FrameBuffer(data, frameMat.Width, frameMat.Height, settings.PixelFormat)
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }, ct);
    }

    /// <summary>
    /// Updates camera settings without closing/reopening device.
    /// </summary>
    /// <param name="settings">New camera settings.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if camera is not open.</exception>
    public Task UpdateSettingsAsync(CameraSettings settings, CancellationToken ct = default)
    {
        if (_capture is null)
            throw new InvalidOperationException("Camera not open");

        // Offload blocking OpenCV property calls to thread pool (finding P1-274)
        var capture = _capture;
        return Task.Run(() =>
        {
            capture.Set(VideoCaptureProperties.FrameWidth, settings.Width);
            capture.Set(VideoCaptureProperties.FrameHeight, settings.Height);
            capture.Set(VideoCaptureProperties.Fps, settings.FrameRate);

            _currentSettings = settings;
        }, ct);
    }

    /// <summary>
    /// Disposes the camera and releases resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
    }
}
