using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Camera;

/// <summary>
/// Configuration for camera devices.
/// </summary>
/// <remarks>
/// Specifies resolution, pixel format, frame rate, and device identifier for camera capture.
/// Settings can be updated dynamically without reopening the device.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Camera settings (EDGE-07)")]
public sealed record CameraSettings
{
    /// <summary>
    /// Frame width in pixels. Default: 1920 (1080p).
    /// </summary>
    public int Width { get; init; } = 1920;

    /// <summary>
    /// Frame height in pixels. Default: 1080 (1080p).
    /// </summary>
    public int Height { get; init; } = 1080;

    /// <summary>
    /// Pixel format for captured frames. Default: RGB24.
    /// </summary>
    public PixelFormat PixelFormat { get; init; } = PixelFormat.RGB24;

    /// <summary>
    /// Frame rate (frames per second). Default: 30 fps.
    /// </summary>
    public int FrameRate { get; init; } = 30;

    /// <summary>
    /// Device path or identifier. Platform-specific:
    /// - Linux: /dev/video0, /dev/video1, etc.
    /// - Windows: Device index as string (0, 1, 2, etc.)
    /// - Null/empty: Use default camera (index 0).
    /// </summary>
    public string? DevicePath { get; init; }
}

/// <summary>
/// Pixel format for camera frames.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Pixel format enumeration (EDGE-07)")]
public enum PixelFormat
{
    /// <summary>
    /// 24-bit RGB (8 bits per channel, no alpha).
    /// </summary>
    RGB24,

    /// <summary>
    /// 32-bit RGBA (8 bits per channel, includes alpha).
    /// </summary>
    RGBA32,

    /// <summary>
    /// YUV 4:2:0 planar format (common for video codecs).
    /// </summary>
    YUV420,

    /// <summary>
    /// Motion JPEG compressed format.
    /// </summary>
    MJPEG
}
