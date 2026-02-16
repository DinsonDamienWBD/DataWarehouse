using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Camera;

/// <summary>
/// Camera device interface for USB, CSI, and IP cameras.
/// </summary>
/// <remarks>
/// <para>
/// Abstracts platform-specific camera access (V4L2 on Linux, DirectShow on Windows, OpenCV cross-platform).
/// Supports dynamic resolution/format changes without device reopen.
/// </para>
/// <para>
/// <strong>Lifecycle:</strong> OpenAsync() → CaptureFrameAsync() (repeatedly) → CloseAsync().
/// Settings can be updated via UpdateSettingsAsync() while device is open.
/// </para>
/// <para>
/// <strong>Frame Capture:</strong> CaptureFrameAsync() returns null if no frame available (device disconnected,
/// timeout, etc.). Check IsOpen before capturing. Frames should be processed quickly to avoid blocking next capture.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Camera device interface (EDGE-07)")]
public interface ICameraDevice : IAsyncDisposable
{
    /// <summary>
    /// Opens the camera device with specified settings.
    /// </summary>
    /// <param name="settings">Camera configuration (resolution, format, FPS).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if device cannot be opened.</exception>
    Task OpenAsync(CameraSettings settings, CancellationToken ct = default);

    /// <summary>
    /// Closes the camera device and releases resources.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task CloseAsync(CancellationToken ct = default);

    /// <summary>
    /// Captures a single frame from the camera.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>FrameBuffer containing captured frame; null if no frame available.</returns>
    /// <exception cref="InvalidOperationException">Thrown if camera is not open.</exception>
    Task<FrameBuffer?> CaptureFrameAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates camera settings without closing/reopening device.
    /// </summary>
    /// <param name="settings">New camera settings.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if camera is not open.</exception>
    Task UpdateSettingsAsync(CameraSettings settings, CancellationToken ct = default);

    /// <summary>
    /// Gets whether the camera device is currently open.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Gets the current camera settings.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if camera is not open.</exception>
    CameraSettings CurrentSettings { get; }
}
