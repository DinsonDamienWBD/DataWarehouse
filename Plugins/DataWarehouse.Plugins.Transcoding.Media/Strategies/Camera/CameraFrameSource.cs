using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Media;
using DataWarehouse.SDK.Edge.Camera;

namespace DataWarehouse.Plugins.Transcoding.Media.Strategies.Camera;

/// <summary>
/// Camera frame source strategy for UltimateMedia integration (Phase 36, EDGE-07).
/// </summary>
/// <remarks>
/// <para>
/// Integrates camera capture into the Media plugin, enabling video processing pipelines
/// to use live camera feeds as input sources. Frames are captured via ICameraDevice and
/// provided to transcoding/streaming strategies.
/// </para>
/// <para>
/// <strong>Typical Use Cases:</strong>
/// <list type="bullet">
///   <item><description>Surveillance: Real-time encoding to H.264/H.265</description></item>
///   <item><description>Object detection: Frame-by-frame analysis with ML models</description></item>
///   <item><description>Live streaming: Camera to RTSP/WebRTC/HLS pipeline</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Lifecycle:</strong> StartAsync() opens camera → CaptureFrameAsync() repeatedly
/// retrieves frames → StopAsync() closes camera. Frame rate controlled by CameraSettings.FrameRate.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Camera frame source strategy (EDGE-07)")]
public sealed class CameraFrameSource : MediaStrategyBase
{
    private readonly ICameraDevice _camera;
    private readonly CameraSettings _settings;
    private bool _isRunning;

    /// <summary>
    /// Initializes a new camera frame source.
    /// </summary>
    /// <param name="settings">Camera configuration (resolution, format, FPS).</param>
    public CameraFrameSource(CameraSettings settings)
        : base(new MediaCapabilities
        {
            SupportedInputFormats = new HashSet<SDK.Contracts.Media.MediaFormat>(),
            SupportedOutputFormats = new HashSet<SDK.Contracts.Media.MediaFormat>(),
            SupportsMetadataExtraction = false,
            SupportsThumbnailGeneration = false,
            SupportsStreaming = false
        })
    {
        _settings = settings;
        _camera = new CameraFrameGrabber();
    }

    public override string StrategyId => "camera-frame-source";
    public override string Name => "Camera Frame Source";

    /// <summary>
    /// Starts the camera capture stream.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _camera.OpenAsync(_settings, ct);
        _isRunning = true;
    }

    /// <summary>
    /// Stops the camera capture stream and releases resources.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task StopAsync(CancellationToken ct = default)
    {
        _isRunning = false;
        await _camera.CloseAsync(ct);
    }

    /// <summary>
    /// Captures a single frame from the camera.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Frame data as byte array; null if no frame available.</returns>
    /// <exception cref="InvalidOperationException">Thrown if camera is not started.</exception>
    public async Task<byte[]?> CaptureFrameAsync(CancellationToken ct = default)
    {
        if (!_isRunning)
            throw new InvalidOperationException("Camera is not started. Call StartAsync() first.");

        var frameBuffer = await _camera.CaptureFrameAsync(ct);
        return frameBuffer?.Data.ToArray();
    }

    /// <summary>
    /// Gets whether the camera is currently capturing.
    /// </summary>
    public bool IsRunning => _isRunning;

    protected override Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("CameraFrameSource is a frame source, not a transcoder. Use captured frames with codec strategies.");
    }

    protected override Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Metadata extraction not supported for live camera feeds.");
    }

    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Thumbnail generation not supported for live camera feeds.");
    }

    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, SDK.Contracts.Media.MediaFormat targetFormat, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Streaming not supported directly. Use CaptureFrameAsync() with streaming strategies.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _camera?.DisposeAsync().AsTask().Wait();
        }
        base.Dispose(disposing);
    }
}
