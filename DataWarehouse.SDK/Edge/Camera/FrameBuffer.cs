using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Camera;

/// <summary>
/// Zero-copy frame buffer for captured camera frames.
/// </summary>
/// <remarks>
/// <para>
/// Provides read-only access to captured frame data without copying. Caller owns the underlying
/// byte array and is responsible for returning it to the pool (if pooled) after use.
/// </para>
/// <para>
/// <strong>Lifetime:</strong> FrameBuffer is valid until Dispose() is called or the underlying
/// byte array is reused. Copy data if longer retention is needed.
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> Not thread-safe. Frame access should be single-threaded or
/// protected by external synchronization.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Camera frame buffer (EDGE-07)")]
public sealed class FrameBuffer : IDisposable
{
    private readonly byte[] _data;
    private readonly int _width;
    private readonly int _height;
    private readonly PixelFormat _format;

    /// <summary>
    /// Initializes a new frame buffer.
    /// </summary>
    /// <param name="data">Byte array containing frame data. Caller retains ownership.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="format">Pixel format of frame data.</param>
    public FrameBuffer(byte[] data, int width, int height, PixelFormat format)
    {
        _data = data;
        _width = width;
        _height = height;
        _format = format;
    }

    /// <summary>
    /// Gets the frame data as a read-only span (zero-copy access).
    /// </summary>
    public ReadOnlySpan<byte> Data => _data;

    /// <summary>
    /// Gets the frame width in pixels.
    /// </summary>
    public int Width => _width;

    /// <summary>
    /// Gets the frame height in pixels.
    /// </summary>
    public int Height => _height;

    /// <summary>
    /// Gets the pixel format of the frame.
    /// </summary>
    public PixelFormat Format => _format;

    /// <summary>
    /// Gets the capture timestamp (Unix milliseconds).
    /// </summary>
    public long Timestamp { get; init; }

    /// <summary>
    /// Disposes the frame buffer. Does not free underlying array (caller owns it).
    /// </summary>
    public void Dispose()
    {
        // No-op -- caller owns buffer and is responsible for returning to pool
    }
}
