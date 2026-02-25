namespace DataWarehouse.SDK.Contracts.Media;

/// <summary>
/// Specifies media container and streaming formats.
/// </summary>
public enum MediaFormat
{
    /// <summary>
    /// Unknown or unsupported format.
    /// </summary>
    Unknown = 0,

    // Video Container Formats
    /// <summary>
    /// MPEG-4 Part 14 container format (.mp4).
    /// </summary>
    MP4 = 1,

    /// <summary>
    /// WebM container format (.webm).
    /// </summary>
    WebM = 2,

    /// <summary>
    /// Matroska container format (.mkv).
    /// </summary>
    MKV = 3,

    /// <summary>
    /// Audio Video Interleave format (.avi).
    /// </summary>
    AVI = 4,

    /// <summary>
    /// QuickTime file format (.mov).
    /// </summary>
    MOV = 5,

    /// <summary>
    /// Flash Video format (.flv).
    /// </summary>
    FLV = 6,

    // Streaming Formats
    /// <summary>
    /// HTTP Live Streaming format (Apple HLS with .m3u8 playlist).
    /// </summary>
    HLS = 100,

    /// <summary>
    /// Dynamic Adaptive Streaming over HTTP (MPEG-DASH with .mpd manifest).
    /// </summary>
    DASH = 101,

    /// <summary>
    /// Microsoft Smooth Streaming format.
    /// </summary>
    SmoothStreaming = 102,

    /// <summary>
    /// Common Media Application Format (CMAF) with fragmented MP4 segments.
    /// </summary>
    CMAF = 103,

    // Audio Formats
    /// <summary>
    /// MPEG-1 or MPEG-2 Audio Layer III (.mp3).
    /// </summary>
    MP3 = 200,

    /// <summary>
    /// Advanced Audio Coding (.aac, .m4a).
    /// </summary>
    AAC = 201,

    /// <summary>
    /// Vorbis audio codec (typically in .ogg containers).
    /// </summary>
    Vorbis = 202,

    /// <summary>
    /// Opus audio codec (typically in .opus or .webm containers).
    /// </summary>
    Opus = 203,

    /// <summary>
    /// Free Lossless Audio Codec (.flac).
    /// </summary>
    FLAC = 204,

    /// <summary>
    /// Waveform Audio File Format (.wav).
    /// </summary>
    WAV = 205,

    // Image Formats (for thumbnails)
    /// <summary>
    /// Joint Photographic Experts Group format (.jpg, .jpeg).
    /// </summary>
    JPEG = 300,

    /// <summary>
    /// Portable Network Graphics format (.png).
    /// </summary>
    PNG = 301,

    /// <summary>
    /// WebP image format (.webp).
    /// </summary>
    WebP = 302,

    /// <summary>
    /// AV1 Image File Format (.avif).
    /// </summary>
    AVIF = 303,

    // RAW Camera Formats
    /// <summary>
    /// Canon RAW version 2 format (.cr2).
    /// </summary>
    CR2 = 400,

    /// <summary>
    /// Nikon Electronic Format (.nef).
    /// </summary>
    NEF = 401,

    /// <summary>
    /// Sony Alpha RAW format (.arw).
    /// </summary>
    ARW = 402,

    /// <summary>
    /// Adobe Digital Negative format (.dng).
    /// </summary>
    DNG = 403,

    // GPU Texture Formats
    /// <summary>
    /// DirectDraw Surface format (.dds).
    /// </summary>
    DDS = 500,

    /// <summary>
    /// Khronos Texture format (.ktx, .ktx2).
    /// </summary>
    KTX = 501,

    // 3D Model Formats
    /// <summary>
    /// GL Transmission Format 2.0 (.gltf, .glb).
    /// </summary>
    GLTF = 600,

    /// <summary>
    /// Universal Scene Description format (.usd, .usda, .usdc).
    /// </summary>
    USD = 601
}

/// <summary>
/// Represents video or image resolution in pixels.
/// </summary>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
public readonly record struct Resolution(int Width, int Height)
{
    /// <summary>
    /// Gets the total pixel count (width × height).
    /// </summary>
    public long PixelCount => (long)Width * Height;

    /// <summary>
    /// Gets the aspect ratio as a decimal value (width ÷ height).
    /// </summary>
    public double AspectRatio => Height > 0 ? (double)Width / Height : 0;

    /// <summary>
    /// Returns a string representation of the resolution in "WxH" format.
    /// </summary>
    public override string ToString() => $"{Width}x{Height}";

    // Common Resolutions
    /// <summary>Standard definition: 640x480 pixels.</summary>
    public static readonly Resolution SD = new(640, 480);
    /// <summary>High definition: 1280x720 pixels (720p).</summary>
    public static readonly Resolution HD = new(1280, 720);
    /// <summary>Full high definition: 1920x1080 pixels (1080p).</summary>
    public static readonly Resolution FullHD = new(1920, 1080);
    /// <summary>Quad high definition: 2560x1440 pixels (1440p).</summary>
    public static readonly Resolution QHD = new(2560, 1440);
    /// <summary>Ultra high definition: 3840x2160 pixels (4K).</summary>
    public static readonly Resolution UHD = new(3840, 2160);
    /// <summary>8K resolution: 7680x4320 pixels.</summary>
    public static readonly Resolution EightK = new(7680, 4320);
}

/// <summary>
/// Represents bitrate in bits per second with common presets.
/// </summary>
/// <param name="BitsPerSecond">The bitrate value in bits per second.</param>
public readonly record struct Bitrate(long BitsPerSecond)
{
    /// <summary>
    /// Gets the bitrate in kilobits per second.
    /// </summary>
    public double Kbps => BitsPerSecond / 1_000.0;

    /// <summary>
    /// Gets the bitrate in megabits per second.
    /// </summary>
    public double Mbps => BitsPerSecond / 1_000_000.0;

    /// <summary>
    /// Returns a human-readable bitrate string.
    /// </summary>
    public override string ToString()
    {
        if (BitsPerSecond >= 1_000_000)
            return $"{Mbps:F2} Mbps";
        if (BitsPerSecond >= 1_000)
            return $"{Kbps:F0} kbps";
        return $"{BitsPerSecond} bps";
    }

    // Common Bitrates
    /// <summary>Low quality audio: 128 kbps.</summary>
    public static readonly Bitrate AudioLow = new(128_000);
    /// <summary>Standard audio: 256 kbps.</summary>
    public static readonly Bitrate AudioStandard = new(256_000);
    /// <summary>High quality audio: 320 kbps.</summary>
    public static readonly Bitrate AudioHigh = new(320_000);
    /// <summary>Low quality video: 500 kbps.</summary>
    public static readonly Bitrate VideoLow = new(500_000);
    /// <summary>Standard definition video: 2 Mbps.</summary>
    public static readonly Bitrate VideoSD = new(2_000_000);
    /// <summary>HD video: 5 Mbps.</summary>
    public static readonly Bitrate VideoHD = new(5_000_000);
    /// <summary>Full HD video: 8 Mbps.</summary>
    public static readonly Bitrate VideoFullHD = new(8_000_000);
    /// <summary>4K video: 25 Mbps.</summary>
    public static readonly Bitrate Video4K = new(25_000_000);
}

/// <summary>
/// Specifies options for media transcoding operations.
/// </summary>
/// <param name="TargetFormat">The desired output media format.</param>
/// <param name="VideoCodec">
/// The video codec to use (e.g., "h264", "h265", "vp9"). Null to preserve original codec.
/// </param>
/// <param name="AudioCodec">
/// The audio codec to use (e.g., "aac", "opus", "mp3"). Null to preserve original codec.
/// </param>
/// <param name="TargetResolution">
/// The desired output resolution. Null to preserve original resolution.
/// </param>
/// <param name="TargetBitrate">
/// The desired output bitrate. Null to use codec defaults.
/// </param>
/// <param name="FrameRate">
/// The target frame rate in frames per second. Null to preserve original frame rate.
/// </param>
/// <param name="TwoPass">
/// Indicates whether to use two-pass encoding for better quality (slower processing).
/// </param>
/// <param name="ProgressCallback">
/// Optional callback to report transcoding progress (0.0 to 1.0).
/// </param>
/// <param name="CustomMetadata">
/// Optional key-value pairs for advanced transcoding parameters such as watermarking,
/// HDR tone mapping, rotation, cropping, interpolation modes, and codec-specific options.
/// </param>
public sealed record TranscodeOptions(
    MediaFormat TargetFormat,
    string? VideoCodec = null,
    string? AudioCodec = null,
    Resolution? TargetResolution = null,
    Bitrate? TargetBitrate = null,
    double? FrameRate = null,
    bool TwoPass = false,
    Action<double>? ProgressCallback = null,
    IReadOnlyDictionary<string, string>? CustomMetadata = null);

/// <summary>
/// Contains metadata extracted from a media file.
/// </summary>
/// <param name="Duration">The total duration of the media content.</param>
/// <param name="Format">The detected media format.</param>
/// <param name="VideoCodec">The video codec used, if applicable.</param>
/// <param name="AudioCodec">The audio codec used, if applicable.</param>
/// <param name="Resolution">The video resolution, if applicable.</param>
/// <param name="Bitrate">The overall bitrate of the media.</param>
/// <param name="FrameRate">The video frame rate, if applicable.</param>
/// <param name="AudioChannels">The number of audio channels (e.g., 2 for stereo, 6 for 5.1).</param>
/// <param name="SampleRate">The audio sample rate in Hz, if applicable.</param>
/// <param name="FileSize">The file size in bytes.</param>
/// <param name="Title">The media title metadata, if available.</param>
/// <param name="Author">The author/artist metadata, if available.</param>
/// <param name="Copyright">The copyright information, if available.</param>
/// <param name="CreationDate">The creation date metadata, if available.</param>
/// <param name="CustomMetadata">Additional custom metadata key-value pairs.</param>
public sealed record MediaMetadata(
    TimeSpan Duration,
    MediaFormat Format,
    string? VideoCodec,
    string? AudioCodec,
    Resolution? Resolution,
    Bitrate Bitrate,
    double? FrameRate,
    int? AudioChannels,
    int? SampleRate,
    long FileSize,
    string? Title = null,
    string? Author = null,
    string? Copyright = null,
    DateTimeOffset? CreationDate = null,
    IReadOnlyDictionary<string, string>? CustomMetadata = null)
{
    /// <summary>
    /// Indicates whether this is a video file (has video codec).
    /// </summary>
    public bool IsVideo => VideoCodec is not null;

    /// <summary>
    /// Indicates whether this is an audio-only file (has audio codec but no video codec).
    /// </summary>
    public bool IsAudioOnly => AudioCodec is not null && VideoCodec is null;

    /// <summary>
    /// Gets a human-readable summary of the media metadata.
    /// </summary>
    public override string ToString()
    {
        var parts = new List<string> { Format.ToString() };
        if (Resolution.HasValue)
            parts.Add(Resolution.Value.ToString());
        if (VideoCodec is not null)
            parts.Add($"video:{VideoCodec}");
        if (AudioCodec is not null)
            parts.Add($"audio:{AudioCodec}");
        parts.Add(Duration.ToString(@"hh\:mm\:ss"));
        return string.Join(", ", parts);
    }
}
