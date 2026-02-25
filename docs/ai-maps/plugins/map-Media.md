# Plugin: Media
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.Transcoding.Media

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/MediaTranscodingPlugin.cs
```csharp
public class MediaTranscodingPlugin : MediaTranscodingPluginBase
{
#endregion
}
    public const string PluginId = "com.datawarehouse.transcoding.media";
    public event EventHandler<TranscodingProgressEventArgs>? ProgressUpdated;
    public event EventHandler<TranscodingCompletedEventArgs>? JobCompleted;
    public MediaTranscodingPlugin();
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override int MaxConcurrentJobs;;
    public override bool HardwareAccelerationAvailable;;
    public override async Task StartAsync(CancellationToken ct);
    public override async Task StopAsync();
    public override async Task<MediaFormat> DetectFormatAsync(string path, CancellationToken ct = default);
    public (MediaFormat Format, string MimeType) NegotiateFormat(string acceptHeader, MediaFormat sourceFormat);
    public static class QualityPresets;
    public override Task<TranscodingProfile?> GetProfileAsync(string profileId, CancellationToken ct = default);
    public override Task<TranscodingProfile> RegisterProfileAsync(TranscodingProfile profile, CancellationToken ct = default);
    public async Task<HlsManifest> GenerateHlsManifestAsync(string sourcePath, string outputDir, int segmentDuration = 6, CancellationToken ct = default);
    public async Task<DashManifest> GenerateDashManifestAsync(string sourcePath, string outputDir, int segmentDuration = 4, CancellationToken ct = default);
    public string BuildHlsTranscodingCommand(string sourcePath, HlsVariant variant, string outputDir);
    public async Task<TranscodingJob> SubmitJobWithPriorityAsync(string sourcePath, string targetPath, TranscodingProfile profile, int priority, CancellationToken ct = default);
    public CachedTranscodingResult? GetCachedResult(string sourcePath, string profileId);
    public void CacheResult(string sourcePath, string profileId, TranscodingResult result, TimeSpan? ttl = null);
    public class WatermarkConfig;
    public enum WatermarkType;
    public enum WatermarkPosition;
    public string BuildWatermarkFilter(WatermarkConfig config, int videoWidth, int videoHeight);
    public string BuildImageMagickWatermarkCommand(string sourcePath, string outputPath, WatermarkConfig config);
    protected virtual void OnProgressUpdated(TranscodingProgressEventArgs e);
    protected virtual void OnJobCompleted(TranscodingCompletedEventArgs e);
    protected void UpdateProgress(string jobId, double progress, string? currentOperation = null);
    public string BuildFFmpegCommand(string sourcePath, string outputPath, TranscodingProfile profile, WatermarkConfig? watermark = null);
    public string BuildFFmpegThumbnailCommand(string sourcePath, string outputPath, TranscodingProfile profile, TimeSpan? timestamp = null);
    public double ParseFFmpegProgress(string output, TimeSpan totalDuration);
    public string BuildImageMagickCommand(string sourcePath, string outputPath, TranscodingProfile profile);
    public string BuildImageMagickBatchCommand(string inputPattern, string outputDir, TranscodingProfile profile);
    public class DocumentConversionPath;
    public DocumentConversionPath GetDocumentConversionPath(MediaFormat source, MediaFormat target);
    public IReadOnlyList<DocumentConversionPath> GetSupportedDocumentConversions();
    public override async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default);
    protected override async Task ProcessJobAsync(TranscodingJob job, CancellationToken ct);
    protected override Task CancelProcessingAsync(string jobId, CancellationToken ct);
    protected override Dictionary<string, object> GetMetadata();
}
```
```csharp
public static class QualityPresets
{
}
    public static TranscodingProfile UHD4K;;
    public static TranscodingProfile FullHD1080p;;
    public static TranscodingProfile HD720p;;
    public static TranscodingProfile SD480p;;
    public static TranscodingProfile VideoThumbnail;;
    public static TranscodingProfile AudioOnly;;
}
```
```csharp
public class WatermarkConfig
{
}
    public WatermarkType Type { get; init; };
    public string? Text { get; init; }
    public string? ImagePath { get; init; }
    public WatermarkPosition Position { get; init; };
    public double Opacity { get; init; };
    public int FontSize { get; init; };
    public string FontColor { get; init; };
    public int Margin { get; init; };
    public double Scale { get; init; };
}
```
```csharp
public class DocumentConversionPath
{
}
    public MediaFormat SourceFormat { get; init; }
    public MediaFormat TargetFormat { get; init; }
    public string Converter { get; init; };
    public string CommandTemplate { get; init; };
    public bool IsSupported { get; init; }
}
```
```csharp
internal class TranscodingJobContext
{
}
    public required TranscodingJob Job { get; init; }
    public required CancellationTokenSource CancellationTokenSource { get; init; }
    public DateTime StartedAt { get; init; }
    public double Progress { get; set; }
}
```
```csharp
public class CachedTranscodingResult
{
}
    public string CacheKey { get; init; };
    public string SourcePath { get; init; };
    public string ProfileId { get; init; };
    public required TranscodingResult Result { get; init; }
    public DateTime CachedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
}
```
```csharp
public class TranscodingProgressEventArgs : EventArgs
{
}
    public string JobId { get; init; };
    public double Progress { get; init; }
    public string? CurrentOperation { get; init; }
    public DateTime Timestamp { get; init; }
}
```
```csharp
public class TranscodingCompletedEventArgs : EventArgs
{
}
    public string JobId { get; init; };
    public bool Success { get; init; }
    public required TranscodingResult Result { get; init; }
    public bool FromCache { get; init; }
}
```
```csharp
public class HlsManifest
{
}
    public string MasterPlaylistPath { get; init; };
    public IReadOnlyList<HlsVariant> Variants { get; init; };
    public int SegmentDuration { get; init; }
    public TimeSpan? MediaDuration { get; init; }
}
```
```csharp
public class HlsVariant
{
}
    public string ProfileId { get; init; };
    public int Bandwidth { get; init; }
    public string Resolution { get; init; };
    public string Codecs { get; init; };
    public string PlaylistPath { get; init; };
    public int SegmentDuration { get; init; }
}
```
```csharp
public class DashManifest
{
}
    public string MpdPath { get; init; };
    public IReadOnlyList<DashRepresentation> Representations { get; init; };
    public int SegmentDuration { get; init; }
    public TimeSpan? MediaDuration { get; init; }
    public TimeSpan MinBufferTime { get; init; }
}
```
```csharp
public class DashRepresentation
{
}
    public string Id { get; init; };
    public string ProfileId { get; init; };
    public int Bandwidth { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRate { get; init; }
    public string Codecs { get; init; };
    public string MimeType { get; init; };
    public int? AudioSampleRate { get; init; }
    public int? AudioChannels { get; init; }
    public string SegmentTemplate { get; init; };
    public string InitializationSegment { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Execution/FfmpegTranscodeHelper.cs
```csharp
public static class FfmpegTranscodeHelper
{
}
    public static async Task<Stream> ExecuteOrPackageAsync(string ffmpegArgs, byte[] sourceBytes, Func<Task<Stream>> packageWriter, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Execution/MediaFormatDetector.cs
```csharp
public static class MediaFormatDetector
{
}
    public static MediaFormat DetectFormat(byte[] data);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Execution/TranscodePackageExecutor.cs
```csharp
public sealed class TranscodePackageExecutor
{
}
    public TranscodePackageExecutor(FfmpegExecutor? ffmpegExecutor = null);
    public bool IsAvailable;;
    public async Task<Stream> ExecutePackageAsync(Stream packageStream, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Execution/FfmpegExecutor.cs
```csharp
public sealed class FfmpegExecutor
{
}
    public FfmpegExecutor(string? ffmpegPath = null, TimeSpan? defaultTimeout = null);
    public bool IsAvailable { get; private set; }
    public string FfmpegPath;;
    public static string FindFfmpeg();
    public async Task<FfmpegResult> ExecuteAsync(string arguments, byte[]? inputData = null, string? workingDirectory = null, TimeSpan? timeout = null, Action<double>? progressCallback = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed record FfmpegResult
{
}
    public required int ExitCode { get; init; }
    public required byte[] OutputData { get; init; }
    public required string StandardError { get; init; }
    public required TimeSpan Duration { get; init; }
    public bool Success;;
}
```
```csharp
public sealed class FfmpegNotFoundException : Exception
{
}
    public FfmpegNotFoundException(string message) : base(message);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/Video/Vp9CodecStrategy.cs
```csharp
internal sealed class Vp9CodecStrategy : MediaStrategyBase
{
}
    public Vp9CodecStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.MOV, MediaFormat.AVI, MediaFormat.WebM, MediaFormat.FLV }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.WebM, MediaFormat.MKV }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: Resolution.UHD, MaxBitrate: 50_000_000, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vp9", "libvpx-vp9" }, SupportsThumbnailGeneration: true, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: false));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override async Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override async Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/Video/AdvancedVideoStrategies.cs
```csharp
internal sealed class Stereo3DVideoStrategy : MediaStrategyBase
{
}
    public Stereo3DVideoStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.MOV }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: Resolution.UHD, MaxBitrate: Bitrate.Video4K.BitsPerSecond, SupportedCodecs: new HashSet<string> { "stereo3d", "h264", "h265", "av1" }, SupportsThumbnailGeneration: false, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: false));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public async Task<Stream> ConvertStereoFormatAsync(Stream input, StereoLayout inputLayout, StereoLayout outputLayout, CancellationToken cancellationToken = default);
    public async Task<Stream> ExtractDepthMapAsync(Stream stereoInput, StereoLayout layout, CancellationToken cancellationToken = default);
    protected override Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override Task<MediaMetadata> ExtractMetadataAsyncCore(Stream inputStream, CancellationToken cancellationToken);
    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream inputStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);;
    protected override Task<Uri> StreamAsyncCore(Stream inputStream, MediaFormat targetFormat, CancellationToken cancellationToken);;
}
```
```csharp
internal sealed class Video360Strategy : MediaStrategyBase
{
}
    public Video360Strategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.WebM }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV }, SupportsStreaming: true, SupportsAdaptiveBitrate: true, MaxResolution: Resolution.EightK, // 360 video typically 4K-8K equirectangular
 MaxBitrate: Bitrate.Video4K.BitsPerSecond, SupportedCodecs: new HashSet<string> { "h264", "h265", "av1", "vp9", "equirect", "cubemap" }, SupportsThumbnailGeneration: false, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: false));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public async Task<CubemapResult> ConvertToCubemapAsync(Stream equirectangularInput, int faceSize = 1024, CancellationToken cancellationToken = default);
    public async Task<Stream> ExtractViewportAsync(Stream input360, float yawDegrees, float pitchDegrees, float fovDegrees = 90, Resolution? outputResolution = null, CancellationToken cancellationToken = default);
    protected override Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override Task<MediaMetadata> ExtractMetadataAsyncCore(Stream inputStream, CancellationToken cancellationToken);
    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream inputStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);;
    protected override Task<Uri> StreamAsyncCore(Stream inputStream, MediaFormat targetFormat, CancellationToken cancellationToken);;
}
```
```csharp
internal sealed class VrVideoStrategy : MediaStrategyBase
{
}
    public VrVideoStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV }, SupportsStreaming: true, SupportsAdaptiveBitrate: true, MaxResolution: Resolution.EightK, MaxBitrate: Bitrate.Video4K.BitsPerSecond, SupportedCodecs: new HashSet<string> { "h265", "av1", "vr-viewport" }, SupportsThumbnailGeneration: false, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: false));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public async Task<VrFrameResult> RenderViewportFrameAsync(Stream vrInput, VrOrientation orientation, VrRenderConfig config, CancellationToken cancellationToken = default);
    public FoveatedRegionMap GenerateFoveatedRegions(float gazeX, float gazeY, Resolution frameResolution);
    protected override Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override Task<MediaMetadata> ExtractMetadataAsyncCore(Stream inputStream, CancellationToken cancellationToken);
    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream inputStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);;
    protected override Task<Uri> StreamAsyncCore(Stream inputStream, MediaFormat targetFormat, CancellationToken cancellationToken);;
}
```
```csharp
internal sealed class HdrToneMappingStrategy : MediaStrategyBase
{
}
    public HdrToneMappingStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.MOV }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: Resolution.EightK, MaxBitrate: Bitrate.Video4K.BitsPerSecond, SupportedCodecs: new HashSet<string> { "h264", "h265", "av1", "tone-map" }, SupportsThumbnailGeneration: false, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: false));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public async Task<ToneMappingResult> ToneMapAsync(Stream hdrInput, ToneMappingOperator toneMapOp, ToneMappingConfig? config = null, CancellationToken cancellationToken = default);
    protected override Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override Task<MediaMetadata> ExtractMetadataAsyncCore(Stream inputStream, CancellationToken cancellationToken);
    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream inputStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);;
    protected override Task<Uri> StreamAsyncCore(Stream inputStream, MediaFormat targetFormat, CancellationToken cancellationToken);;
}
```
```csharp
public sealed class CubemapResult
{
}
    public required int FaceSize { get; init; }
    public required string Layout { get; init; }
    public required string OutputFormat { get; init; }
}
```
```csharp
public sealed class VrOrientation
{
}
    public required float Yaw { get; init; }
    public required float Pitch { get; init; }
    public required float Roll { get; init; }
}
```
```csharp
public sealed class VrRenderConfig
{
}
    public bool EnableFoveation { get; init; };
    public float TargetFrameRate { get; init; };
    public float MaxMotionToPhotonMs { get; init; };
    public Resolution EyeBufferResolution { get; init; };
}
```
```csharp
public sealed class VrFrameResult
{
}
    public required byte[] FrameData { get; init; }
    public required VrOrientation Orientation { get; init; }
    public required long RenderTimeMs { get; init; }
    public required long MotionToPhotonMs { get; init; }
    public required int FoveatedRegions { get; init; }
}
```
```csharp
public sealed class FoveatedRegionMap
{
}
    public required FoveatedRegion CenterRegion { get; init; }
    public required FoveatedRegion MidRegion { get; init; }
    public required float PeripheralQualityLevel { get; init; }
}
```
```csharp
public sealed class FoveatedRegion
{
}
    public required float CenterX { get; init; }
    public required float CenterY { get; init; }
    public required float Radius { get; init; }
    public required float QualityLevel { get; init; }
}
```
```csharp
public sealed class ToneMappingConfig
{
}
    public float PeakLuminanceNits { get; init; };
    public float DesaturationStrength { get; init; };
    public float Exposure { get; init; };
}
```
```csharp
public sealed class ToneMappingResult
{
}
    public required ToneMappingOperator Operator { get; init; }
    public required string InputColorSpace { get; init; }
    public required string OutputColorSpace { get; init; }
    public required float PeakLuminanceNits { get; init; }
    public required string FilterChain { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/Video/Av1CodecStrategy.cs
```csharp
internal sealed class Av1CodecStrategy : MediaStrategyBase
{
}
    public Av1CodecStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.MOV, MediaFormat.AVI, MediaFormat.WebM }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.WebM }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: Resolution.EightK, MaxBitrate: 100_000_000, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "av1", "libaom-av1", "libsvtav1" }, SupportsThumbnailGeneration: true, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: false));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override async Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override async Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/Video/AiProcessingStrategies.cs
```csharp
internal sealed class OnnxInferenceStrategy : MediaStrategyBase
{
}
    public OnnxInferenceStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.JPEG, MediaFormat.PNG, MediaFormat.WAV }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.JPEG, MediaFormat.PNG }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: Resolution.UHD, MaxBitrate: null, SupportedCodecs: new HashSet<string> { "onnx-inference" }, SupportsThumbnailGeneration: false, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: true));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public OnnxModelInfo LoadModel(string modelName, string modelPath, string? version = null);
    public OnnxModelInfo HotSwapModel(string modelName, string newModelPath, string newVersion);
    public async Task<InferenceResult> RunInferenceAsync(string modelName, byte[] inputData, CancellationToken cancellationToken = default);
    public IReadOnlyList<OnnxModelInfo> GetLoadedModels();;
    protected override Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override Task<MediaMetadata> ExtractMetadataAsyncCore(Stream inputStream, CancellationToken cancellationToken);
    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream inputStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);;
    protected override Task<Uri> StreamAsyncCore(Stream inputStream, MediaFormat targetFormat, CancellationToken cancellationToken);;
}
```
```csharp
internal sealed class AiUpscalingStrategy : MediaStrategyBase
{
}
    public AiUpscalingStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.JPEG, MediaFormat.PNG, MediaFormat.WebP }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.PNG, MediaFormat.JPEG }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: Resolution.EightK, MaxBitrate: null, SupportedCodecs: new HashSet<string> { "ai-upscale" }, SupportsThumbnailGeneration: false, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: true));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public void Configure(string modelPath, int scaleFactor = 2, int tileSize = 512, int tileOverlap = 32);
    public async Task<UpscaleResult> UpscaleAsync(Stream inputImage, int scaleFactor, CancellationToken cancellationToken = default);
    protected override Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override Task<MediaMetadata> ExtractMetadataAsyncCore(Stream inputStream, CancellationToken cancellationToken);
    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream inputStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);;
    protected override Task<Uri> StreamAsyncCore(Stream inputStream, MediaFormat targetFormat, CancellationToken cancellationToken);;
}
```
```csharp
internal sealed class ObjectDetectionStrategy : MediaStrategyBase
{
}
    public ObjectDetectionStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.JPEG, MediaFormat.PNG, MediaFormat.MP4 }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.JPEG, MediaFormat.PNG }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: Resolution.UHD, MaxBitrate: null, SupportedCodecs: new HashSet<string> { "yolo-detection" }, SupportsThumbnailGeneration: false, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: true));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public void Configure(string modelPath, float confidenceThreshold = 0.5f, float iouThreshold = 0.45f, string[]? classLabels = null);
    public async Task<DetectionResult> DetectObjectsAsync(Stream inputImage, CancellationToken cancellationToken = default);
    public List<Detection> ApplyNms(List<Detection> detections, float iouThreshold);
    protected override Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);;
    protected override Task<MediaMetadata> ExtractMetadataAsyncCore(Stream inputStream, CancellationToken cancellationToken);
    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream inputStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);;
    protected override Task<Uri> StreamAsyncCore(Stream inputStream, MediaFormat targetFormat, CancellationToken cancellationToken);;
}
```
```csharp
internal sealed class FaceDetectionStrategy : MediaStrategyBase
{
}
    public FaceDetectionStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.JPEG, MediaFormat.PNG }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.JPEG, MediaFormat.PNG }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: Resolution.UHD, MaxBitrate: null, SupportedCodecs: new HashSet<string> { "face-detection" }, SupportsThumbnailGeneration: false, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: true));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public async Task<FaceDetectionResult> DetectFacesAsync(Stream inputImage, CancellationToken cancellationToken = default);
    protected override Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);;
    protected override Task<MediaMetadata> ExtractMetadataAsyncCore(Stream inputStream, CancellationToken cancellationToken);
    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream inputStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);;
    protected override Task<Uri> StreamAsyncCore(Stream inputStream, MediaFormat targetFormat, CancellationToken cancellationToken);;
}
```
```csharp
internal sealed class SpeechToTextStrategy : MediaStrategyBase
{
}
    public SpeechToTextStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.WAV, MediaFormat.MP3, MediaFormat.FLAC, MediaFormat.Opus, MediaFormat.AAC, MediaFormat.MP4, MediaFormat.MKV }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.Unknown // Text output, not media
 }, SupportsStreaming: true, SupportsAdaptiveBitrate: false, MaxResolution: default, MaxBitrate: null, SupportedCodecs: new HashSet<string> { "whisper-stt" }, SupportsThumbnailGeneration: false, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: true));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public async Task<TranscriptionResult> TranscribeAsync(Stream audioStream, CancellationToken cancellationToken = default);
    protected override Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);;
    protected override Task<MediaMetadata> ExtractMetadataAsyncCore(Stream inputStream, CancellationToken cancellationToken);
    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream inputStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);;
    protected override Task<Uri> StreamAsyncCore(Stream inputStream, MediaFormat targetFormat, CancellationToken cancellationToken);;
}
```
```csharp
public sealed class OnnxModelInfo
{
}
    public required string ModelName { get; init; }
    public required string ModelPath { get; init; }
    public required string Version { get; init; }
    public required DateTime LoadedAt { get; init; }
    public required long FileSizeBytes { get; init; }
    public required bool UseMemoryMapping { get; init; }
    public required string ExecutionProvider { get; init; }
    public required int[] InputShape { get; init; }
    public required int[] OutputShape { get; init; }
    public required bool IsLoaded { get; init; }
}
```
```csharp
public sealed class InferenceResult
{
}
    public required string ModelName { get; init; }
    public required string ModelVersion { get; init; }
    public required string ExecutionProvider { get; init; }
    public required long InferenceTimeMs { get; init; }
    public required byte[] OutputData { get; init; }
    public required int[] OutputShape { get; init; }
    public required float Confidence { get; init; }
    public required string[] Labels { get; init; }
}
```
```csharp
public sealed class UpscaleResult
{
}
    public required int ScaleFactor { get; init; }
    public required int TilesProcessed { get; init; }
    public required long ProcessingTimeMs { get; init; }
    public required long OutputSizeBytes { get; init; }
    public required string ModelUsed { get; init; }
}
```
```csharp
public sealed class DetectionResult
{
}
    public required Detection[] Detections { get; init; }
    public required long ProcessingTimeMs { get; init; }
    public required string ModelUsed { get; init; }
    public required float ConfidenceThreshold { get; init; }
    public required float IouThreshold { get; init; }
}
```
```csharp
public sealed class Detection
{
}
    public required BoundingBox BoundingBox { get; init; }
    public required string ClassLabel { get; init; }
    public required int ClassId { get; init; }
    public required float Confidence { get; init; }
}
```
```csharp
public sealed class BoundingBox
{
}
    public required float X { get; init; }
    public required float Y { get; init; }
    public required float Width { get; init; }
    public required float Height { get; init; }
}
```
```csharp
public sealed class FaceDetectionResult
{
}
    public required FaceInfo[] Faces { get; init; }
    public required long ProcessingTimeMs { get; init; }
    public required string ModelUsed { get; init; }
    public required float ConfidenceThreshold { get; init; }
}
```
```csharp
public sealed class FaceInfo
{
}
    public required BoundingBox BoundingBox { get; init; }
    public required float Confidence { get; init; }
    public FaceLandmarks? Landmarks { get; init; }
}
```
```csharp
public sealed class FaceLandmarks
{
}
    public required (float X, float Y) LeftEye { get; init; }
    public required (float X, float Y) RightEye { get; init; }
    public required (float X, float Y) Nose { get; init; }
    public required (float X, float Y) LeftMouth { get; init; }
    public required (float X, float Y) RightMouth { get; init; }
}
```
```csharp
public sealed class TranscriptionResult
{
}
    public required string Text { get; init; }
    public required string Language { get; init; }
    public required float LanguageConfidence { get; init; }
    public required TranscriptionSegment[] Segments { get; init; }
    public required long ProcessingTimeMs { get; init; }
    public required long AudioDurationMs { get; init; }
    public required string ModelUsed { get; init; }
}
```
```csharp
public sealed class TranscriptionSegment
{
}
    public required string Text { get; init; }
    public required long StartMs { get; init; }
    public required long EndMs { get; init; }
    public required float Confidence { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/Video/H264CodecStrategy.cs
```csharp
internal sealed class H264CodecStrategy : MediaStrategyBase
{
}
    public H264CodecStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.MOV, MediaFormat.AVI, MediaFormat.WebM, MediaFormat.FLV }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.MOV, MediaFormat.FLV, MediaFormat.HLS }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: Resolution.UHD, MaxBitrate: 50_000_000, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264", "avc", "libx264", "h264_nvenc", "h264_qsv" }, SupportsThumbnailGeneration: true, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: true));
    public override string StrategyId;;
    public override string Name;;
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override async Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override async Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/Video/GpuAccelerationStrategies.cs
```csharp
internal sealed class GpuAccelerationStrategy : MediaStrategyBase
{
}
    public GpuAccelerationStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.MOV, MediaFormat.AVI, MediaFormat.WebM }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.WebM }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: Resolution.EightK, MaxBitrate: Bitrate.Video4K.BitsPerSecond, SupportedCodecs: new HashSet<string> { "h264_nvenc", "hevc_nvenc", "av1_nvenc", "h264_qsv", "hevc_qsv", "av1_qsv", "h264_amf", "hevc_amf", "av1_amf", "h264", "h265", "av1" }, SupportsThumbnailGeneration: true, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: true));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public async Task<GpuDetectionResult> DetectGpuHardwareAsync(CancellationToken cancellationToken = default);
    public bool CheckGpuMemory(long requiredBytes);
    public GpuMemoryAllocation AllocateGpuMemory(long bytes, string purpose);
    public void ReleaseGpuMemory(GpuMemoryAllocation allocation);
    public string GetEncoderArgs(string codec);
    public GpuHealthStats GetHealthStats();
    protected override Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override Task<MediaMetadata> ExtractMetadataAsyncCore(Stream inputStream, CancellationToken cancellationToken);
    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream inputStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream inputStream, MediaFormat targetFormat, CancellationToken cancellationToken);;
}
```
```csharp
public sealed class GpuDetectionResult
{
}
    public required List<GpuDeviceInfo> AvailableGpus { get; init; }
    public required HardwareEncoder ActiveEncoder { get; init; }
    public required int SelectedGpuIndex { get; init; }
    public required bool IsCached { get; init; }
}
```
```csharp
public sealed class GpuDeviceInfo
{
}
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required GpuVendor Vendor { get; init; }
    public required int TotalMemoryMb { get; init; }
    public required int FreeMemoryMb { get; init; }
    public required int Utilization { get; init; }
    public required int Temperature { get; init; }
    public required string[] SupportedEncoders { get; init; }
}
```
```csharp
public sealed class GpuMemoryAllocation
{
}
    public required string AllocationId { get; init; }
    public required long AllocatedBytes { get; init; }
    public required string Purpose { get; init; }
    public required bool IsCpuFallback { get; init; }
    public required int GpuIndex { get; init; }
}
```
```csharp
public sealed class GpuHealthStats
{
}
    public required HardwareEncoder ActiveEncoder { get; init; }
    public required int SelectedGpuIndex { get; init; }
    public required long MemoryAllocatedBytes { get; init; }
    public required long MemoryLimitBytes { get; init; }
    public required double MemoryUtilization { get; init; }
    public required int GpuCount { get; init; }
    public required List<GpuDeviceInfo> Gpus { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/Video/H265CodecStrategy.cs
```csharp
internal sealed class H265CodecStrategy : MediaStrategyBase
{
}
    public H265CodecStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.MOV, MediaFormat.AVI, MediaFormat.WebM }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.MOV }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: Resolution.EightK, MaxBitrate: 100_000_000, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h265", "hevc", "libx265", "hevc_nvenc", "hevc_qsv" }, SupportsThumbnailGeneration: true, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: true));
    public override string StrategyId;;
    public override string Name;;
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override async Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override async Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/Video/VvcCodecStrategy.cs
```csharp
internal sealed class VvcCodecStrategy : MediaStrategyBase
{
}
    public VvcCodecStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.MOV, MediaFormat.AVI, MediaFormat.WebM }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: Resolution.EightK, MaxBitrate: 100_000_000, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vvc", "h266", "libvvenc" }, SupportsThumbnailGeneration: false, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: false));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override async Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/ThreeD/GltfModelStrategy.cs
```csharp
internal sealed class GltfModelStrategy : MediaStrategyBase
{
#endregion
}
    public GltfModelStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.GLTF }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.GLTF }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: null, MaxBitrate: null, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "gltf", "gltf2", "glb", "draco" }, SupportsThumbnailGeneration: false, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: false));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override async Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
    internal sealed class GltfRoot;
    internal sealed class GltfAsset;
    internal sealed class GltfScene;
    internal sealed class GltfNode;
    internal sealed class GltfMesh;
    internal sealed class GltfPrimitive;
    internal sealed class GltfAttributes;
    internal sealed class GltfMaterial;
    internal sealed class GltfPbrMetallicRoughness;
    internal sealed class GltfTexture;
    internal sealed class GltfImage;
    internal sealed class GltfAccessor;
    internal sealed class GltfBufferView;
    internal sealed class GltfBuffer;
    internal sealed class GltfAnimation;
    internal sealed class GltfAnimationChannel;
    internal sealed class GltfAnimationTarget;
    internal sealed class GltfAnimationSampler;
    internal sealed class GltfSkin;
}
```
```csharp
private sealed class SceneStats
{
}
    public long TotalVertices { get; set; }
    public long TotalTriangles { get; set; }
    public bool HasMorphTargets { get; set; }
    public TimeSpan AnimationDuration { get; set; }
}
```
```csharp
internal sealed class GltfRoot
{
}
    [JsonPropertyName("asset")]
public GltfAsset? Asset { get; set; }
    [JsonPropertyName("scene")]
public int? Scene { get; set; }
    [JsonPropertyName("scenes")]
public GltfScene[]? Scenes { get; set; }
    [JsonPropertyName("nodes")]
public GltfNode[]? Nodes { get; set; }
    [JsonPropertyName("meshes")]
public GltfMesh[]? Meshes { get; set; }
    [JsonPropertyName("materials")]
public GltfMaterial[]? Materials { get; set; }
    [JsonPropertyName("textures")]
public GltfTexture[]? Textures { get; set; }
    [JsonPropertyName("images")]
public GltfImage[]? Images { get; set; }
    [JsonPropertyName("accessors")]
public GltfAccessor[]? Accessors { get; set; }
    [JsonPropertyName("bufferViews")]
public GltfBufferView[]? BufferViews { get; set; }
    [JsonPropertyName("buffers")]
public GltfBuffer[]? Buffers { get; set; }
    [JsonPropertyName("animations")]
public GltfAnimation[]? Animations { get; set; }
    [JsonPropertyName("skins")]
public GltfSkin[]? Skins { get; set; }
    [JsonPropertyName("extensionsUsed")]
public string[]? ExtensionsUsed { get; set; }
    [JsonPropertyName("extensionsRequired")]
public string[]? ExtensionsRequired { get; set; }
}
```
```csharp
internal sealed class GltfAsset
{
}
    [JsonPropertyName("version")]
public string? Version { get; set; }
    [JsonPropertyName("generator")]
public string? Generator { get; set; }
    [JsonPropertyName("copyright")]
public string? Copyright { get; set; }
    [JsonPropertyName("minVersion")]
public string? MinVersion { get; set; }
}
```
```csharp
internal sealed class GltfScene
{
}
    [JsonPropertyName("name")]
public string? Name { get; set; }
    [JsonPropertyName("nodes")]
public int[]? Nodes { get; set; }
}
```
```csharp
internal sealed class GltfNode
{
}
    [JsonPropertyName("name")]
public string? Name { get; set; }
    [JsonPropertyName("mesh")]
public int? Mesh { get; set; }
    [JsonPropertyName("skin")]
public int? Skin { get; set; }
    [JsonPropertyName("children")]
public int[]? Children { get; set; }
    [JsonPropertyName("translation")]
public double[]? Translation { get; set; }
    [JsonPropertyName("rotation")]
public double[]? Rotation { get; set; }
    [JsonPropertyName("scale")]
public double[]? Scale { get; set; }
    [JsonPropertyName("matrix")]
public double[]? Matrix { get; set; }
}
```
```csharp
internal sealed class GltfMesh
{
}
    [JsonPropertyName("name")]
public string? Name { get; set; }
    [JsonPropertyName("primitives")]
public GltfPrimitive[]? Primitives { get; set; }
}
```
```csharp
internal sealed class GltfPrimitive
{
}
    [JsonPropertyName("attributes")]
public GltfAttributes? Attributes { get; set; }
    [JsonPropertyName("indices")]
public int? Indices { get; set; }
    [JsonPropertyName("material")]
public int? Material { get; set; }
    [JsonPropertyName("mode")]
public int? Mode { get; set; }
    [JsonPropertyName("targets")]
public JsonElement[]? Targets { get; set; }
}
```
```csharp
internal sealed class GltfAttributes
{
}
    [JsonPropertyName("POSITION")]
public int? Position { get; set; }
    [JsonPropertyName("NORMAL")]
public int? Normal { get; set; }
    [JsonPropertyName("TANGENT")]
public int? Tangent { get; set; }
    [JsonPropertyName("TEXCOORD_0")]
public int? TexCoord0 { get; set; }
    [JsonPropertyName("TEXCOORD_1")]
public int? TexCoord1 { get; set; }
    [JsonPropertyName("COLOR_0")]
public int? Color0 { get; set; }
    [JsonPropertyName("JOINTS_0")]
public int? Joints0 { get; set; }
    [JsonPropertyName("WEIGHTS_0")]
public int? Weights0 { get; set; }
}
```
```csharp
internal sealed class GltfMaterial
{
}
    [JsonPropertyName("name")]
public string? Name { get; set; }
    [JsonPropertyName("pbrMetallicRoughness")]
public GltfPbrMetallicRoughness? PbrMetallicRoughness { get; set; }
    [JsonPropertyName("doubleSided")]
public bool? DoubleSided { get; set; }
    [JsonPropertyName("alphaMode")]
public string? AlphaMode { get; set; }
}
```
```csharp
internal sealed class GltfPbrMetallicRoughness
{
}
    [JsonPropertyName("baseColorFactor")]
public double[]? BaseColorFactor { get; set; }
    [JsonPropertyName("metallicFactor")]
public double? MetallicFactor { get; set; }
    [JsonPropertyName("roughnessFactor")]
public double? RoughnessFactor { get; set; }
}
```
```csharp
internal sealed class GltfTexture
{
}
    [JsonPropertyName("sampler")]
public int? Sampler { get; set; }
    [JsonPropertyName("source")]
public int? Source { get; set; }
}
```
```csharp
internal sealed class GltfImage
{
}
    [JsonPropertyName("uri")]
public string? Uri { get; set; }
    [JsonPropertyName("mimeType")]
public string? MimeType { get; set; }
    [JsonPropertyName("bufferView")]
public int? BufferView { get; set; }
}
```
```csharp
internal sealed class GltfAccessor
{
}
    [JsonPropertyName("bufferView")]
public int? BufferView { get; set; }
    [JsonPropertyName("byteOffset")]
public int? ByteOffset { get; set; }
    [JsonPropertyName("componentType")]
public int ComponentType { get; set; }
    [JsonPropertyName("count")]
public int Count { get; set; }
    [JsonPropertyName("type")]
public string? Type { get; set; }
    [JsonPropertyName("max")]
public double[]? Max { get; set; }
    [JsonPropertyName("min")]
public double[]? Min { get; set; }
}
```
```csharp
internal sealed class GltfBufferView
{
}
    [JsonPropertyName("buffer")]
public int Buffer { get; set; }
    [JsonPropertyName("byteOffset")]
public int? ByteOffset { get; set; }
    [JsonPropertyName("byteLength")]
public int ByteLength { get; set; }
    [JsonPropertyName("byteStride")]
public int? ByteStride { get; set; }
    [JsonPropertyName("target")]
public int? Target { get; set; }
}
```
```csharp
internal sealed class GltfBuffer
{
}
    [JsonPropertyName("uri")]
public string? Uri { get; set; }
    [JsonPropertyName("byteLength")]
public int ByteLength { get; set; }
}
```
```csharp
internal sealed class GltfAnimation
{
}
    [JsonPropertyName("name")]
public string? Name { get; set; }
    [JsonPropertyName("channels")]
public GltfAnimationChannel[]? Channels { get; set; }
    [JsonPropertyName("samplers")]
public GltfAnimationSampler[]? Samplers { get; set; }
}
```
```csharp
internal sealed class GltfAnimationChannel
{
}
    [JsonPropertyName("sampler")]
public int Sampler { get; set; }
    [JsonPropertyName("target")]
public GltfAnimationTarget? Target { get; set; }
}
```
```csharp
internal sealed class GltfAnimationTarget
{
}
    [JsonPropertyName("node")]
public int? Node { get; set; }
    [JsonPropertyName("path")]
public string? Path { get; set; }
}
```
```csharp
internal sealed class GltfAnimationSampler
{
}
    [JsonPropertyName("input")]
public int Input { get; set; }
    [JsonPropertyName("output")]
public int Output { get; set; }
    [JsonPropertyName("interpolation")]
public string? Interpolation { get; set; }
}
```
```csharp
internal sealed class GltfSkin
{
}
    [JsonPropertyName("name")]
public string? Name { get; set; }
    [JsonPropertyName("inverseBindMatrices")]
public int? InverseBindMatrices { get; set; }
    [JsonPropertyName("skeleton")]
public int? Skeleton { get; set; }
    [JsonPropertyName("joints")]
public int[]? Joints { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/ThreeD/UsdModelStrategy.cs
```csharp
internal sealed class UsdModelStrategy : MediaStrategyBase
{
}
    public UsdModelStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.USD }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.USD, MediaFormat.GLTF }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: null, MaxBitrate: null, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "usd", "usda", "usdc", "usdz" }, SupportsThumbnailGeneration: false, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: false));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override async Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
}
```
```csharp
private sealed class UsdSceneInfo
{
}
    public string DefaultPrim { get; set; };
    public string UpAxis { get; set; };
    public double MetersPerUnit { get; set; };
    public int PrimCount { get; set; }
    public int MeshCount { get; set; }
    public int MaterialCount { get; set; }
    public bool HasSkeleton { get; set; }
    public bool HasAnimation { get; set; }
    public double AnimationEndFrame { get; set; }
    public int VariantSetCount { get; set; }
    public int SublayerCount { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/GPUTexture/KtxTextureStrategy.cs
```csharp
internal sealed class KtxTextureStrategy : MediaStrategyBase
{
}
    public KtxTextureStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.KTX, MediaFormat.PNG, MediaFormat.JPEG, MediaFormat.DDS }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.KTX }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: new Resolution(16384, 16384), MaxBitrate: null, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ktx", "ktx2", "basis-etc1s", "basis-uastc", "etc2", "astc", "pvrtc" }, SupportsThumbnailGeneration: true, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: true));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override async Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override async Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/GPUTexture/DdsTextureStrategy.cs
```csharp
internal sealed class DdsTextureStrategy : MediaStrategyBase
{
}
    public DdsTextureStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.DDS, MediaFormat.PNG, MediaFormat.JPEG }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.DDS }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: new Resolution(16384, 16384), MaxBitrate: null, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dds", "bc1", "dxt1", "bc3", "dxt5", "bc4", "bc5", "bc6h", "bc7" }, SupportsThumbnailGeneration: true, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: true));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override async Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override async Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/Streaming/CmafStreamingStrategy.cs
```csharp
internal sealed class CmafStreamingStrategy : MediaStrategyBase
{
}
    public CmafStreamingStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.MOV, MediaFormat.AVI, MediaFormat.WebM }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.CMAF, MediaFormat.HLS, MediaFormat.DASH }, SupportsStreaming: true, SupportsAdaptiveBitrate: true, MaxResolution: Resolution.UHD, MaxBitrate: 25_000_000, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264", "h265", "vp9", "av1", "aac", "opus" }, SupportsThumbnailGeneration: false, SupportsMetadataExtraction: false, SupportsHardwareAcceleration: true));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override async Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/Streaming/DashStreamingStrategy.cs
```csharp
internal sealed class DashStreamingStrategy : MediaStrategyBase
{
}
    public DashStreamingStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.MOV, MediaFormat.AVI, MediaFormat.WebM }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.DASH }, SupportsStreaming: true, SupportsAdaptiveBitrate: true, MaxResolution: Resolution.UHD, MaxBitrate: 25_000_000, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264", "h265", "vp9", "av1", "aac", "opus", "ac3", "eac3" }, SupportsThumbnailGeneration: false, SupportsMetadataExtraction: false, SupportsHardwareAcceleration: true));
    public override string StrategyId;;
    public override string Name;;
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override async Task<Uri> StreamAsyncCore(Stream mediaStream, SDK.Contracts.Media.MediaFormat targetFormat, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/Streaming/HlsStreamingStrategy.cs
```csharp
internal sealed class HlsStreamingStrategy : MediaStrategyBase
{
}
    public HlsStreamingStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.MOV, MediaFormat.AVI, MediaFormat.WebM }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.HLS }, SupportsStreaming: true, SupportsAdaptiveBitrate: true, MaxResolution: Resolution.UHD, MaxBitrate: 25_000_000, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264", "aac", "h265", "ac3" }, SupportsThumbnailGeneration: false, SupportsMetadataExtraction: false, SupportsHardwareAcceleration: true));
    public override string StrategyId;;
    public override string Name;;
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override async Task<Uri> StreamAsyncCore(Stream mediaStream, SDK.Contracts.Media.MediaFormat targetFormat, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/Camera/CameraFrameSource.cs
```csharp
[SdkCompatibility("3.0.0", Notes = "Phase 36: Camera frame source strategy (EDGE-07)")]
public sealed class CameraFrameSource : MediaStrategyBase, IAsyncDisposable
{
}
    public CameraFrameSource(CameraSettings settings) : base(new MediaCapabilities { SupportedInputFormats = new HashSet<SDK.Contracts.Media.MediaFormat>(), SupportedOutputFormats = new HashSet<SDK.Contracts.Media.MediaFormat>(), SupportsMetadataExtraction = false, SupportsThumbnailGeneration = false, SupportsStreaming = false });
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public async Task StartAsync(CancellationToken ct = default);
    public async Task StopAsync(CancellationToken ct = default);
    public async Task<byte[]?> CaptureFrameAsync(CancellationToken ct = default);
    public bool IsRunning;;
    protected override Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, SDK.Contracts.Media.MediaFormat targetFormat, CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
    public new async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/RAW/NefRawStrategy.cs
```csharp
internal sealed class NefRawStrategy : MediaStrategyBase
{
}
    public NefRawStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.NEF }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.JPEG, MediaFormat.PNG, MediaFormat.AVIF }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: new Resolution(8256, 5504), MaxBitrate: null, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "nef", "nikon-raw", "nrw" }, SupportsThumbnailGeneration: true, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: false));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override async Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override async Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/RAW/Cr2RawStrategy.cs
```csharp
internal sealed class Cr2RawStrategy : MediaStrategyBase
{
}
    public Cr2RawStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.CR2 }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.JPEG, MediaFormat.PNG, MediaFormat.AVIF }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: new Resolution(8192, 5464), MaxBitrate: null, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cr2", "canon-raw", "lossless-jpeg" }, SupportsThumbnailGeneration: true, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: false));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override async Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override async Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/RAW/DngRawStrategy.cs
```csharp
internal sealed class DngRawStrategy : MediaStrategyBase
{
}
    public DngRawStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.DNG }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.JPEG, MediaFormat.PNG, MediaFormat.AVIF, MediaFormat.DNG }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: new Resolution(16384, 16384), MaxBitrate: null, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dng", "linear-dng", "cinema-dng", "dng-1.7" }, SupportsThumbnailGeneration: true, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: false));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override async Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override async Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/RAW/ArwRawStrategy.cs
```csharp
internal sealed class ArwRawStrategy : MediaStrategyBase
{
}
    public ArwRawStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.ARW }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.JPEG, MediaFormat.PNG, MediaFormat.AVIF }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: new Resolution(9504, 6336), MaxBitrate: null, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "arw", "sony-raw", "sr2" }, SupportsThumbnailGeneration: true, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: false));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override async Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override async Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/Image/JpegImageStrategy.cs
```csharp
internal sealed class JpegImageStrategy : MediaStrategyBase
{
}
    public JpegImageStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.JPEG, MediaFormat.PNG, MediaFormat.WebP, MediaFormat.AVIF }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.JPEG }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: new Resolution(65535, 65535), MaxBitrate: null, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "jpeg", "jpg", "jfif", "progressive-jpeg" }, SupportsThumbnailGeneration: true, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: false));
    public override string StrategyId;;
    public override string Name;;
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override async Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override async Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/Image/AvifImageStrategy.cs
```csharp
internal sealed class AvifImageStrategy : MediaStrategyBase
{
}
    public AvifImageStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.AVIF, MediaFormat.JPEG, MediaFormat.PNG, MediaFormat.WebP }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.AVIF }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: new Resolution(65536, 65536), MaxBitrate: null, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "avif", "av1", "avif-hdr", "avif-lossless" }, SupportsThumbnailGeneration: true, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: true));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override async Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override async Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/Image/WebPImageStrategy.cs
```csharp
internal sealed class WebPImageStrategy : MediaStrategyBase
{
}
    public WebPImageStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.WebP, MediaFormat.JPEG, MediaFormat.PNG, MediaFormat.AVIF }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.WebP }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: new Resolution(16383, 16383), MaxBitrate: null, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "webp", "webp-lossy", "webp-lossless", "webp-animated" }, SupportsThumbnailGeneration: true, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: false));
    public override string StrategyId;;
    public override string Name;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override async Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override async Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.Transcoding.Media/Strategies/Image/PngImageStrategy.cs
```csharp
internal sealed class PngImageStrategy : MediaStrategyBase
{
}
    public PngImageStrategy() : base(new MediaCapabilities(SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.PNG, MediaFormat.JPEG, MediaFormat.WebP, MediaFormat.AVIF }, SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.PNG }, SupportsStreaming: false, SupportsAdaptiveBitrate: false, MaxResolution: new Resolution(2147483647, 2147483647), MaxBitrate: null, SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "png", "apng", "png-interlaced" }, SupportsThumbnailGeneration: true, SupportsMetadataExtraction: true, SupportsHardwareAcceleration: false));
    public override string StrategyId;;
    public override string Name;;
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task<Stream> TranscodeAsyncCore(Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken);
    protected override async Task<MediaMetadata> ExtractMetadataAsyncCore(Stream mediaStream, CancellationToken cancellationToken);
    protected override async Task<Stream> GenerateThumbnailAsyncCore(Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken);
    protected override Task<Uri> StreamAsyncCore(Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken);
}
```
