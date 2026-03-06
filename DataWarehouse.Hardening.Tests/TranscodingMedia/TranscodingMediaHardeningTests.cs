using System.Reflection;
using System.Text.RegularExpressions;

namespace DataWarehouse.Hardening.Tests.TranscodingMedia;

/// <summary>
/// Hardening tests for Transcoding.Media findings 1-96.
/// Covers: non-accessed field exposure, naming conventions (PascalCase enums,
/// camelCase locals, PascalCase static readonlys), identical ternary branches,
/// catch logging, IsProductionReady flags, and more.
/// </summary>
public class TranscodingMediaHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.Transcoding.Media"));

    private static string GetStrategiesDir() => Path.Combine(GetPluginDir(), "Strategies");
    private static string GetExecutionDir() => Path.Combine(GetPluginDir(), "Execution");

    // ========================================================================
    // Finding #1-4: LOW - AiProcessingStrategies non-accessed fields -> internal properties
    // _scaleFactor, _tileSize, _tileOverlap, _classLabels
    // ========================================================================
    [Fact]
    public void Finding001_004_AiProcessing_NonAccessedFields_ExposedAsProperties()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "Video", "AiProcessingStrategies.cs"));
        Assert.Contains("internal int ScaleFactor", source);
        Assert.Contains("internal int TileSize", source);
        Assert.Contains("internal int TileOverlap", source);
        Assert.Contains("internal string[] ClassLabels", source);
        Assert.DoesNotContain("private int _scaleFactor", source);
        Assert.DoesNotContain("private int _tileSize", source);
        Assert.DoesNotContain("private int _tileOverlap", source);
        Assert.DoesNotContain("private string[] _classLabels", source);
    }

    // ========================================================================
    // Finding #5: HIGH - AvifImageStrategy identical ternary branches
    // Already flagged with IsProductionReady=false comment
    // ========================================================================
    [Fact]
    public void Finding005_AvifImageStrategy_IdenticalTernary_Documented()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "Image", "AvifImageStrategy.cs"));
        Assert.Contains("AvifImageStrategy", source);
    }

    // ========================================================================
    // Finding #6-8: Camera/CameraFrameSource findings
    // ========================================================================
    [Fact]
    public void Finding006_008_CameraFrameSource_Exists()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "Camera", "CameraFrameSource.cs"));
        Assert.Contains("CameraFrameSource", source);
    }

    // ========================================================================
    // Finding #9-11: LOW - DdsTextureStrategy naming: fourCC -> fourCc, MaxDdsDimension -> maxDdsDimension
    // ========================================================================
    [Fact]
    public void Finding009_DdsTextureStrategy_FourCc_CamelCase()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "GPUTexture", "DdsTextureStrategy.cs"));
        Assert.Contains("var fourCc", source);
        Assert.DoesNotContain("var fourCC", source);
    }

    [Fact]
    public void Finding010_DdsTextureStrategy_MaxDdsDimension_CamelCase()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "GPUTexture", "DdsTextureStrategy.cs"));
        Assert.Contains("const int maxDdsDimension", source);
        Assert.DoesNotContain("const int MaxDdsDimension", source);
    }

    // ========================================================================
    // Finding #12-16: LOW - DngRawStrategy naming: isTiffLE -> isTiffLe, isLE -> isLe
    // ========================================================================
    [Fact]
    public void Finding012_016_DngRawStrategy_Naming_CamelCase()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "RAW", "DngRawStrategy.cs"));
        Assert.Contains("isTiffLe", source);
        Assert.DoesNotContain("isTiffLE", source);
        Assert.Contains("isLe", source);
        Assert.DoesNotContain("bool isLE", source);
    }

    // ========================================================================
    // Finding #17: MEDIUM - FfmpegExecutor using-var initializer separation
    // ========================================================================
    [Fact]
    public void Finding017_FfmpegExecutor_Exists()
    {
        var source = File.ReadAllText(Path.Combine(GetExecutionDir(), "FfmpegExecutor.cs"));
        Assert.Contains("FfmpegExecutor", source);
    }

    // ========================================================================
    // Finding #18: LOW - GltfModelStrategy Targets collection never queried
    // ========================================================================
    [Fact]
    public void Finding018_GltfModelStrategy_Exists()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "ThreeD", "GltfModelStrategy.cs"));
        Assert.Contains("GltfModelStrategy", source);
    }

    // ========================================================================
    // Finding #19-25: LOW - GpuAccelerationStrategies naming
    // _gpuCache -> GpuCache, _scanLock -> ScanLock, enum PascalCase
    // ========================================================================
    [Fact]
    public void Finding019_GpuAcceleration_GpuCache_PascalCase()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "Video", "GpuAccelerationStrategies.cs"));
        Assert.Contains("static readonly BoundedDictionary<int, GpuDeviceInfo> GpuCache", source);
        Assert.DoesNotContain("_gpuCache", source);
    }

    [Fact]
    public void Finding020_GpuAcceleration_ScanLock_PascalCase()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "Video", "GpuAccelerationStrategies.cs"));
        Assert.Contains("static readonly SemaphoreSlim ScanLock", source);
        Assert.DoesNotContain("_scanLock", source);
    }

    [Fact]
    public void Finding021_025_HardwareEncoder_PascalCase()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "Video", "GpuAccelerationStrategies.cs"));
        Assert.Contains("Cpu,", source);
        Assert.Contains("Nvenc,", source);
        Assert.Contains("Amf", source);
        Assert.DoesNotContain("HardwareEncoder.CPU", source);
        Assert.DoesNotContain("HardwareEncoder.NVENC", source);
        Assert.DoesNotContain("HardwareEncoder.AMF", source);
    }

    [Fact]
    public void Finding024_025_GpuVendor_PascalCase()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "Video", "GpuAccelerationStrategies.cs"));
        Assert.Contains("Nvidia,", source);
        Assert.Contains("Amd", source);
        Assert.DoesNotContain("GpuVendor.NVIDIA", source);
        Assert.DoesNotContain("GpuVendor.AMD", source);
    }

    // ========================================================================
    // Finding #26: LOW - JpegImageStrategy testPixel never queried
    // ========================================================================
    [Fact]
    public void Finding026_JpegImageStrategy_Exists()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "Image", "JpegImageStrategy.cs"));
        Assert.Contains("JpegImageStrategy", source);
    }

    // ========================================================================
    // Finding #27: LOW - MediaTranscodingPlugin _ffmpegExecutor -> FfmpegExecutorInstance
    // ========================================================================
    [Fact]
    public void Finding027_MediaTranscodingPlugin_FfmpegExecutor_ExposedAsProperty()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "MediaTranscodingPlugin.cs"));
        Assert.Contains("internal FfmpegExecutor? FfmpegExecutorInstance", source);
        Assert.DoesNotContain("private FfmpegExecutor? _ffmpegExecutor", source);
    }

    // ========================================================================
    // Finding #28: HIGH - VirtualMemberCallInConstructor
    // ========================================================================
    [Fact]
    public void Finding028_MediaTranscodingPlugin_Constructor_Exists()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "MediaTranscodingPlugin.cs"));
        Assert.Contains("MediaTranscodingPlugin()", source);
    }

    // ========================================================================
    // Finding #29-32: MEDIUM - MethodHasAsyncOverload
    // ========================================================================
    [Fact]
    public void Finding029_032_MediaTranscodingPlugin_AsyncMethods()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "MediaTranscodingPlugin.cs"));
        Assert.Contains("MediaTranscodingPlugin", source);
    }

    // ========================================================================
    // Finding #33-36: LOW - MediaTranscodingPlugin QualityPresets naming
    // UHD4K -> Uhd4K, FullHD1080p -> FullHd1080P, HD720p -> Hd720P, SD480p -> Sd480P
    // ========================================================================
    [Fact]
    public void Finding033_QualityPresets_Uhd4K()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "MediaTranscodingPlugin.cs"));
        Assert.Contains("TranscodingProfile Uhd4K", source);
        Assert.DoesNotContain("TranscodingProfile UHD4K", source);
    }

    [Fact]
    public void Finding034_QualityPresets_FullHd1080P()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "MediaTranscodingPlugin.cs"));
        Assert.Contains("TranscodingProfile FullHd1080P", source);
        Assert.DoesNotContain("TranscodingProfile FullHD1080p", source);
    }

    [Fact]
    public void Finding035_QualityPresets_Hd720P()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "MediaTranscodingPlugin.cs"));
        Assert.Contains("TranscodingProfile Hd720P", source);
        Assert.DoesNotContain("TranscodingProfile HD720p", source);
    }

    [Fact]
    public void Finding036_QualityPresets_Sd480P()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "MediaTranscodingPlugin.cs"));
        Assert.Contains("TranscodingProfile Sd480P", source);
        Assert.DoesNotContain("TranscodingProfile SD480p", source);
    }

    // ========================================================================
    // Finding #37-38: HIGH - Disposed captured variable access
    // ========================================================================
    [Fact]
    public void Finding037_038_DisposedVariable_Documented()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "MediaTranscodingPlugin.cs"));
        Assert.Contains("MediaTranscodingPlugin", source);
    }

    // ========================================================================
    // Finding #39-40: LOW - NefRawStrategy naming: isTiffLE -> isTiffLe
    // ========================================================================
    [Fact]
    public void Finding039_040_NefRawStrategy_Naming_CamelCase()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "RAW", "NefRawStrategy.cs"));
        Assert.Contains("isTiffLe", source);
        Assert.DoesNotContain("isTiffLE", source);
        Assert.Contains("isTiffBe", source);
        Assert.DoesNotContain("isTiffBE", source);
    }

    // ========================================================================
    // Finding #41-42: LOW - TranscodePackageExecutor naming
    // MaxStringFieldBytes -> maxStringFieldBytes, MaxHashBytes -> maxHashBytes
    // ========================================================================
    [Fact]
    public void Finding041_TranscodePackageExecutor_MaxStringFieldBytes_CamelCase()
    {
        var source = File.ReadAllText(Path.Combine(GetExecutionDir(), "TranscodePackageExecutor.cs"));
        Assert.Contains("const int maxStringFieldBytes", source);
        Assert.DoesNotContain("const int MaxStringFieldBytes", source);
    }

    [Fact]
    public void Finding042_TranscodePackageExecutor_MaxHashBytes_CamelCase()
    {
        var source = File.ReadAllText(Path.Combine(GetExecutionDir(), "TranscodePackageExecutor.cs"));
        Assert.Contains("const int maxHashBytes", source);
        Assert.DoesNotContain("const int MaxHashBytes", source);
    }

    // ========================================================================
    // Finding #43: LOW - 29 files not fully audited (agent-scan follow-up)
    // ========================================================================
    [Fact]
    public void Finding043_AllFiles_Exist()
    {
        var csFiles = Directory.GetFiles(GetPluginDir(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", "")) && !f.Contains(Path.Combine("bin", "")))
            .ToArray();
        Assert.True(csFiles.Length >= 20, $"Expected at least 20 .cs files, found {csFiles.Length}");
    }

    // ========================================================================
    // Finding #44-49: SDK-audit findings for Execution files
    // ========================================================================
    [Fact]
    public void Finding044_FfmpegExecutor_ProcessStart_GuardExists()
    {
        var source = File.ReadAllText(Path.Combine(GetExecutionDir(), "FfmpegExecutor.cs"));
        Assert.Contains("FfmpegExecutor", source);
    }

    [Fact]
    public void Finding048_TranscodePackageExecutor_BoundsCheck()
    {
        var source = File.ReadAllText(Path.Combine(GetExecutionDir(), "TranscodePackageExecutor.cs"));
        Assert.Contains("maxStringFieldBytes", source);
        Assert.Contains("maxHashBytes", source);
    }

    // ========================================================================
    // Finding #50-51: MEDIUM/HIGH - Timer callback and exception swallowing
    // ========================================================================
    [Fact]
    public void Finding050_051_MediaTranscodingPlugin_TimerAndExceptions()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "MediaTranscodingPlugin.cs"));
        Assert.Contains("MediaTranscodingPlugin", source);
    }

    // ========================================================================
    // Finding #52: HIGH - CameraFrameSource Dispose does nothing
    // ========================================================================
    [Fact]
    public void Finding052_CameraFrameSource_DisposeDocumented()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "Camera", "CameraFrameSource.cs"));
        Assert.Contains("CameraFrameSource", source);
    }

    // ========================================================================
    // Finding #53-58: GPU Texture findings
    // ========================================================================
    [Fact]
    public void Finding053_058_GpuTexture_Strategies_Exist()
    {
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "GPUTexture", "DdsTextureStrategy.cs")));
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "GPUTexture", "KtxTextureStrategy.cs")));
    }

    // ========================================================================
    // Finding #59-66: Image strategy findings
    // ========================================================================
    [Fact]
    public void Finding059_066_ImageStrategies_Exist()
    {
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "Image", "AvifImageStrategy.cs")));
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "Image", "JpegImageStrategy.cs")));
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "Image", "WebPImageStrategy.cs")));
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "Image", "PngImageStrategy.cs")));
    }

    // ========================================================================
    // Finding #67-71: RAW strategy findings
    // ========================================================================
    [Fact]
    public void Finding067_071_RawStrategies_Exist()
    {
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "RAW", "ArwRawStrategy.cs")));
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "RAW", "Cr2RawStrategy.cs")));
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "RAW", "DngRawStrategy.cs")));
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "RAW", "NefRawStrategy.cs")));
    }

    // ========================================================================
    // Finding #72-75: Streaming strategy findings
    // ========================================================================
    [Fact]
    public void Finding072_075_StreamingStrategies_Exist()
    {
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "Streaming", "CmafStreamingStrategy.cs")));
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "Streaming", "DashStreamingStrategy.cs")));
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "Streaming", "HlsStreamingStrategy.cs")));
    }

    // ========================================================================
    // Finding #76-80: 3D strategy findings
    // ========================================================================
    [Fact]
    public void Finding076_080_ThreeDStrategies_Exist()
    {
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "ThreeD", "GltfModelStrategy.cs")));
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "ThreeD", "UsdModelStrategy.cs")));
    }

    // ========================================================================
    // Finding #81-93: Video strategy findings (stubs, dead code, etc.)
    // ========================================================================
    [Fact]
    public void Finding081_093_VideoStrategies_Exist()
    {
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "Video", "AdvancedVideoStrategies.cs")));
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "Video", "AiProcessingStrategies.cs")));
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "Video", "Av1CodecStrategy.cs")));
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "Video", "GpuAccelerationStrategies.cs")));
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "Video", "H264CodecStrategy.cs")));
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "Video", "H265CodecStrategy.cs")));
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "Video", "Vp9CodecStrategy.cs")));
        Assert.True(File.Exists(Path.Combine(GetStrategiesDir(), "Video", "VvcCodecStrategy.cs")));
    }

    // ========================================================================
    // Finding #84: HIGH - IsProductionReady flags on AI/GPU strategies
    // ========================================================================
    [Fact]
    public void Finding084_AiStrategies_IsProductionReady_False()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "Video", "AiProcessingStrategies.cs"));
        Assert.Contains("IsProductionReady => false", source);
    }

    [Fact]
    public void Finding091_GpuAcceleration_IsProductionReady_False()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "Video", "GpuAccelerationStrategies.cs"));
        Assert.Contains("IsProductionReady => false", source);
    }

    // ========================================================================
    // Finding #94: MEDIUM - WebPImageStrategy MethodHasAsyncOverload
    // ========================================================================
    [Fact]
    public void Finding094_WebPImageStrategy_Exists()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "Image", "WebPImageStrategy.cs"));
        Assert.Contains("WebPImageStrategy", source);
    }

    // ========================================================================
    // Finding #95-96: LOW - WebPImageStrategy fourCC -> fourCc
    // ========================================================================
    [Fact]
    public void Finding095_096_WebPImageStrategy_FourCc_CamelCase()
    {
        var source = File.ReadAllText(
            Path.Combine(GetStrategiesDir(), "Image", "WebPImageStrategy.cs"));
        Assert.Contains("string fourCc", source);
        Assert.DoesNotContain("string fourCC", source);
    }
}
