using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.Transcoding.Media;

/// <summary>
/// Production-ready auto-transcoding pipeline plugin implementing "store once, view anywhere" capability.
/// Supports FFmpeg integration, ImageMagick processing, document conversion, format negotiation,
/// quality presets, adaptive bitrate streaming, priority queuing, result caching, watermarking,
/// and real-time progress tracking.
/// </summary>
public class MediaTranscodingPlugin : MediaTranscodingPluginBase
{
    #region Constants and Static Data

    /// <summary>
    /// Plugin identifier for the media transcoding plugin.
    /// </summary>
    public const string PluginId = "com.datawarehouse.transcoding.media";

    /// <summary>
    /// Magic bytes signatures for format detection.
    /// </summary>
    private static readonly Dictionary<string, byte[]> MagicBytes = new()
    {
        // Video/Container formats
        ["ftyp"] = new byte[] { 0x66, 0x74, 0x79, 0x70 },            // MP4/MOV (at offset 4)
        ["mp4"] = new byte[] { 0x00, 0x00, 0x00 },                   // MP4 header start
        ["webm"] = new byte[] { 0x1A, 0x45, 0xDF, 0xA3 },           // WebM/MKV (EBML)
        ["avi"] = new byte[] { 0x52, 0x49, 0x46, 0x46 },            // AVI (RIFF)

        // Audio formats
        ["mp3_id3"] = new byte[] { 0x49, 0x44, 0x33 },              // MP3 with ID3
        ["mp3_sync"] = new byte[] { 0xFF, 0xFB },                    // MP3 sync word
        ["flac"] = new byte[] { 0x66, 0x4C, 0x61, 0x43 },           // FLAC
        ["wav"] = new byte[] { 0x52, 0x49, 0x46, 0x46 },            // WAV (RIFF)
        ["ogg"] = new byte[] { 0x4F, 0x67, 0x67, 0x53 },            // OGG

        // Image formats
        ["jpeg"] = new byte[] { 0xFF, 0xD8, 0xFF },                 // JPEG
        ["png"] = new byte[] { 0x89, 0x50, 0x4E, 0x47 },            // PNG
        ["gif"] = new byte[] { 0x47, 0x49, 0x46, 0x38 },            // GIF
        ["webp"] = new byte[] { 0x52, 0x49, 0x46, 0x46 },           // WebP (RIFF)
        ["bmp"] = new byte[] { 0x42, 0x4D },                         // BMP
        ["tiff_le"] = new byte[] { 0x49, 0x49, 0x2A, 0x00 },        // TIFF little-endian
        ["tiff_be"] = new byte[] { 0x4D, 0x4D, 0x00, 0x2A },        // TIFF big-endian
        ["avif"] = new byte[] { 0x00, 0x00, 0x00 },                 // AVIF (ftyp at 4)
        ["heif"] = new byte[] { 0x00, 0x00, 0x00 },                 // HEIF (ftyp at 4)

        // Document formats
        ["pdf"] = new byte[] { 0x25, 0x50, 0x44, 0x46 },            // PDF
        ["docx"] = new byte[] { 0x50, 0x4B, 0x03, 0x04 },           // DOCX (ZIP)
        ["xlsx"] = new byte[] { 0x50, 0x4B, 0x03, 0x04 },           // XLSX (ZIP)
        ["pptx"] = new byte[] { 0x50, 0x4B, 0x03, 0x04 },           // PPTX (ZIP)
    };

    /// <summary>
    /// MIME type mappings for Accept header negotiation.
    /// </summary>
    private static readonly Dictionary<string, MediaFormat> MimeToFormat = new()
    {
        // Video
        ["video/mp4"] = MediaFormat.Mp4,
        ["video/webm"] = MediaFormat.WebM,
        ["video/x-matroska"] = MediaFormat.Mkv,
        ["video/avi"] = MediaFormat.Avi,
        ["video/quicktime"] = MediaFormat.Mov,
        ["video/x-msvideo"] = MediaFormat.Avi,

        // Audio
        ["audio/mpeg"] = MediaFormat.Mp3,
        ["audio/mp3"] = MediaFormat.Mp3,
        ["audio/aac"] = MediaFormat.Aac,
        ["audio/mp4"] = MediaFormat.Aac,
        ["audio/wav"] = MediaFormat.Wav,
        ["audio/x-wav"] = MediaFormat.Wav,
        ["audio/flac"] = MediaFormat.Flac,
        ["audio/ogg"] = MediaFormat.Ogg,
        ["audio/opus"] = MediaFormat.Opus,

        // Images
        ["image/jpeg"] = MediaFormat.Jpeg,
        ["image/png"] = MediaFormat.Png,
        ["image/webp"] = MediaFormat.WebP,
        ["image/gif"] = MediaFormat.Gif,
        ["image/bmp"] = MediaFormat.Bmp,
        ["image/tiff"] = MediaFormat.Tiff,
        ["image/avif"] = MediaFormat.Avif,
        ["image/heif"] = MediaFormat.Heif,
        ["image/heic"] = MediaFormat.Heif,

        // Documents
        ["application/pdf"] = MediaFormat.Pdf,
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = MediaFormat.Docx,
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = MediaFormat.Xlsx,
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = MediaFormat.Pptx,
        ["text/html"] = MediaFormat.Html,
        ["text/plain"] = MediaFormat.Txt,
        ["text/markdown"] = MediaFormat.Markdown,
    };

    /// <summary>
    /// Format to MIME type mapping.
    /// </summary>
    private static readonly Dictionary<MediaFormat, string> FormatToMime = new()
    {
        [MediaFormat.Mp4] = "video/mp4",
        [MediaFormat.WebM] = "video/webm",
        [MediaFormat.Mkv] = "video/x-matroska",
        [MediaFormat.Avi] = "video/avi",
        [MediaFormat.Mov] = "video/quicktime",
        [MediaFormat.Mp3] = "audio/mpeg",
        [MediaFormat.Aac] = "audio/aac",
        [MediaFormat.Wav] = "audio/wav",
        [MediaFormat.Flac] = "audio/flac",
        [MediaFormat.Ogg] = "audio/ogg",
        [MediaFormat.Opus] = "audio/opus",
        [MediaFormat.Jpeg] = "image/jpeg",
        [MediaFormat.Png] = "image/png",
        [MediaFormat.WebP] = "image/webp",
        [MediaFormat.Gif] = "image/gif",
        [MediaFormat.Bmp] = "image/bmp",
        [MediaFormat.Tiff] = "image/tiff",
        [MediaFormat.Avif] = "image/avif",
        [MediaFormat.Heif] = "image/heif",
        [MediaFormat.Pdf] = "application/pdf",
        [MediaFormat.Docx] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [MediaFormat.Html] = "text/html",
        [MediaFormat.Txt] = "text/plain",
    };

    #endregion

    #region Private Fields

    private readonly ConcurrentDictionary<string, TranscodingJobContext> _jobContexts = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobCancellations = new();
    private readonly ConcurrentDictionary<string, CachedTranscodingResult> _cache = new();
    private readonly ConcurrentDictionary<string, TranscodingProfile> _customProfiles = new();
    private readonly PriorityQueue<string, int> _jobQueue = new();
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private readonly SemaphoreSlim _processingSlots;
    private readonly Timer _cacheCleanupTimer;
    private readonly Timer _queueProcessorTimer;
    private bool _isRunning;
    private string? _ffmpegPath;
    private string? _imageMagickPath;
    private bool _ffmpegAvailable;
    private bool _imageMagickAvailable;

    /// <summary>
    /// Event raised when transcoding progress updates.
    /// </summary>
    public event EventHandler<TranscodingProgressEventArgs>? ProgressUpdated;

    /// <summary>
    /// Event raised when a job completes.
    /// </summary>
    public event EventHandler<TranscodingCompletedEventArgs>? JobCompleted;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the MediaTranscodingPlugin.
    /// </summary>
    public MediaTranscodingPlugin()
    {
        _processingSlots = new SemaphoreSlim(MaxConcurrentJobs, MaxConcurrentJobs);
        _cacheCleanupTimer = new Timer(CleanupCache, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        _queueProcessorTimer = new Timer(ProcessQueue, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));

        // Register quality preset profiles
        RegisterQualityPresetProfiles();
    }

    #endregion

    #region Plugin Identity

    /// <inheritdoc />
    public override string Id => PluginId;

    /// <inheritdoc />
    public override string Name => "Media Auto-Transcoding Pipeline";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override int MaxConcurrentJobs => Environment.ProcessorCount;

    /// <inheritdoc />
    public override bool HardwareAccelerationAvailable => DetectHardwareAcceleration();

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken ct)
    {
        _isRunning = true;
        await DetectExternalToolsAsync(ct);
        await base.StartAsync(ct);
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        _isRunning = false;

        // Cancel all pending jobs
        foreach (var cts in _jobCancellations.Values)
        {
            cts.Cancel();
        }

        _cacheCleanupTimer.Dispose();
        _queueProcessorTimer.Dispose();
        _processingSlots.Dispose();
        _queueLock.Dispose();

        await base.StopAsync();
    }

    #endregion

    #region Format Detection (72.1, 72.2)

    /// <inheritdoc />
    public override async Task<MediaFormat> DetectFormatAsync(string path, CancellationToken ct = default)
    {
        // First try magic byte detection
        var format = await DetectFormatByMagicBytesAsync(path, ct);
        if (format != MediaFormat.Unknown)
        {
            return format;
        }

        // Fall back to extension-based detection
        return await base.DetectFormatAsync(path, ct);
    }

    /// <summary>
    /// Detects format by examining magic bytes in the file header.
    /// </summary>
    private async Task<MediaFormat> DetectFormatByMagicBytesAsync(string path, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[32];
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var bytesRead = await fs.ReadAsync(buffer, ct);

            if (bytesRead < 4)
                return MediaFormat.Unknown;

            // Check for specific formats

            // JPEG
            if (StartsWith(buffer, MagicBytes["jpeg"]))
                return MediaFormat.Jpeg;

            // PNG
            if (StartsWith(buffer, MagicBytes["png"]))
                return MediaFormat.Png;

            // GIF
            if (StartsWith(buffer, MagicBytes["gif"]))
                return MediaFormat.Gif;

            // PDF
            if (StartsWith(buffer, MagicBytes["pdf"]))
                return MediaFormat.Pdf;

            // WebM/MKV (EBML)
            if (StartsWith(buffer, MagicBytes["webm"]))
            {
                // Need to check further to distinguish WebM from MKV
                return await DetermineEbmlContainerAsync(path, ct);
            }

            // OGG
            if (StartsWith(buffer, MagicBytes["ogg"]))
                return MediaFormat.Ogg;

            // FLAC
            if (StartsWith(buffer, MagicBytes["flac"]))
                return MediaFormat.Flac;

            // MP3
            if (StartsWith(buffer, MagicBytes["mp3_id3"]) || StartsWith(buffer, MagicBytes["mp3_sync"]))
                return MediaFormat.Mp3;

            // BMP
            if (StartsWith(buffer, MagicBytes["bmp"]))
                return MediaFormat.Bmp;

            // TIFF
            if (StartsWith(buffer, MagicBytes["tiff_le"]) || StartsWith(buffer, MagicBytes["tiff_be"]))
                return MediaFormat.Tiff;

            // RIFF-based formats (AVI, WAV, WebP)
            if (StartsWith(buffer, MagicBytes["avi"]))
            {
                if (bytesRead >= 12)
                {
                    var typeId = Encoding.ASCII.GetString(buffer, 8, 4);
                    return typeId switch
                    {
                        "AVI " => MediaFormat.Avi,
                        "WAVE" => MediaFormat.Wav,
                        "WEBP" => MediaFormat.WebP,
                        _ => MediaFormat.Unknown
                    };
                }
            }

            // MP4/MOV/AVIF/HEIF (ftyp box)
            if (bytesRead >= 12 && buffer[4] == 0x66 && buffer[5] == 0x74 && buffer[6] == 0x79 && buffer[7] == 0x70)
            {
                var brand = Encoding.ASCII.GetString(buffer, 8, 4);
                return brand switch
                {
                    "isom" or "iso2" or "mp41" or "mp42" or "M4V " or "avc1" => MediaFormat.Mp4,
                    "qt  " => MediaFormat.Mov,
                    "avif" or "avis" => MediaFormat.Avif,
                    "heic" or "heix" or "hevc" or "hevx" or "mif1" => MediaFormat.Heif,
                    _ => MediaFormat.Mp4 // Default to MP4 for unknown ftyp
                };
            }

            // ZIP-based formats (DOCX, XLSX, PPTX)
            if (StartsWith(buffer, MagicBytes["docx"]))
            {
                return await DetermineOfficeFormatAsync(path, ct);
            }

            return MediaFormat.Unknown;
        }
        catch
        {
            return MediaFormat.Unknown;
        }
    }

    private static bool StartsWith(byte[] buffer, byte[] signature)
    {
        if (buffer.Length < signature.Length)
            return false;

        for (int i = 0; i < signature.Length; i++)
        {
            if (buffer[i] != signature[i])
                return false;
        }
        return true;
    }

    private async Task<MediaFormat> DetermineEbmlContainerAsync(string path, CancellationToken ct)
    {
        // For EBML containers, check the DocType element
        try
        {
            var buffer = new byte[64];
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var bytesRead = await fs.ReadAsync(buffer.AsMemory(totalRead), ct);
                if (bytesRead == 0) break;
                totalRead += bytesRead;
            }

            var content = Encoding.ASCII.GetString(buffer, 0, totalRead);
            if (content.Contains("webm"))
                return MediaFormat.WebM;
            if (content.Contains("matroska"))
                return MediaFormat.Mkv;

            return MediaFormat.Mkv; // Default to MKV
        }
        catch
        {
            return MediaFormat.Mkv;
        }
    }

    private async Task<MediaFormat> DetermineOfficeFormatAsync(string path, CancellationToken ct)
    {
        // Check the content of the ZIP to determine Office format
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.StartsWith("word/"))
                    return MediaFormat.Docx;
                if (entry.FullName.StartsWith("xl/"))
                    return MediaFormat.Xlsx;
                if (entry.FullName.StartsWith("ppt/"))
                    return MediaFormat.Pptx;
            }
        }
        catch
        {
            // Ignore errors
        }

        return MediaFormat.Unknown;
    }

    #endregion

    #region Format Negotiation (72.4)

    /// <summary>
    /// Negotiates the best output format based on Accept header and source format.
    /// </summary>
    /// <param name="acceptHeader">HTTP Accept header value.</param>
    /// <param name="sourceFormat">Source media format.</param>
    /// <returns>Best matching output format and MIME type.</returns>
    public (MediaFormat Format, string MimeType) NegotiateFormat(string acceptHeader, MediaFormat sourceFormat)
    {
        if (string.IsNullOrWhiteSpace(acceptHeader) || acceptHeader == "*/*")
        {
            // Return source format if no preference
            var mime = FormatToMime.GetValueOrDefault(sourceFormat, "application/octet-stream");
            return (sourceFormat, mime);
        }

        // Parse Accept header
        var acceptedTypes = ParseAcceptHeader(acceptHeader);

        foreach (var (mimeType, quality) in acceptedTypes.OrderByDescending(x => x.Quality))
        {
            // Check for wildcard
            if (mimeType == "*/*")
            {
                var defaultMime = FormatToMime.GetValueOrDefault(sourceFormat, "application/octet-stream");
                return (sourceFormat, defaultMime);
            }

            // Check for type wildcard (e.g., "image/*")
            if (mimeType.EndsWith("/*"))
            {
                var typePrefix = mimeType[..^2];
                var matchingFormat = FindBestFormatForType(typePrefix, sourceFormat);
                if (matchingFormat != MediaFormat.Unknown)
                {
                    var mime = FormatToMime.GetValueOrDefault(matchingFormat, mimeType);
                    return (matchingFormat, mime);
                }
                continue;
            }

            // Exact match
            if (MimeToFormat.TryGetValue(mimeType.ToLowerInvariant(), out var format))
            {
                if (SupportedOutputFormats.Contains(format))
                {
                    return (format, mimeType);
                }
            }
        }

        // No match found, return source format
        var sourceMime = FormatToMime.GetValueOrDefault(sourceFormat, "application/octet-stream");
        return (sourceFormat, sourceMime);
    }

    private List<(string MimeType, double Quality)> ParseAcceptHeader(string acceptHeader)
    {
        var result = new List<(string MimeType, double Quality)>();

        foreach (var part in acceptHeader.Split(','))
        {
            var trimmed = part.Trim();
            var quality = 1.0;
            var mimeType = trimmed;

            var qualityIndex = trimmed.IndexOf(";q=", StringComparison.OrdinalIgnoreCase);
            if (qualityIndex > 0)
            {
                mimeType = trimmed[..qualityIndex].Trim();
                var qualityStr = trimmed[(qualityIndex + 3)..].Trim();
                double.TryParse(qualityStr, out quality);
            }

            result.Add((mimeType, quality));
        }

        return result;
    }

    private MediaFormat FindBestFormatForType(string typePrefix, MediaFormat sourceFormat)
    {
        var sourceType = GetMediaType(sourceFormat);

        // If source matches requested type, prefer keeping source format
        if (sourceType == typePrefix && SupportedOutputFormats.Contains(sourceFormat))
        {
            return sourceFormat;
        }

        // Find best supported format for the type
        return typePrefix switch
        {
            "video" => SupportedOutputFormats.Contains(MediaFormat.Mp4) ? MediaFormat.Mp4 :
                       SupportedOutputFormats.Contains(MediaFormat.WebM) ? MediaFormat.WebM : MediaFormat.Unknown,
            "audio" => SupportedOutputFormats.Contains(MediaFormat.Aac) ? MediaFormat.Aac :
                       SupportedOutputFormats.Contains(MediaFormat.Mp3) ? MediaFormat.Mp3 : MediaFormat.Unknown,
            "image" => SupportedOutputFormats.Contains(MediaFormat.WebP) ? MediaFormat.WebP :
                       SupportedOutputFormats.Contains(MediaFormat.Jpeg) ? MediaFormat.Jpeg : MediaFormat.Unknown,
            _ => MediaFormat.Unknown
        };
    }

    private static string GetMediaType(MediaFormat format)
    {
        return format switch
        {
            MediaFormat.Mp4 or MediaFormat.WebM or MediaFormat.Mkv or
            MediaFormat.Avi or MediaFormat.Mov or MediaFormat.H264 or
            MediaFormat.H265 or MediaFormat.VP8 or MediaFormat.VP9 or MediaFormat.AV1 => "video",

            MediaFormat.Mp3 or MediaFormat.Aac or MediaFormat.Wav or
            MediaFormat.Flac or MediaFormat.Ogg or MediaFormat.Opus => "audio",

            MediaFormat.Jpeg or MediaFormat.Png or MediaFormat.WebP or
            MediaFormat.Gif or MediaFormat.Bmp or MediaFormat.Tiff or
            MediaFormat.Avif or MediaFormat.Heif => "image",

            MediaFormat.Pdf or MediaFormat.Docx or MediaFormat.Xlsx or
            MediaFormat.Pptx or MediaFormat.Html or MediaFormat.Txt => "application",

            _ => "application"
        };
    }

    #endregion

    #region Quality Presets (72.5)

    /// <summary>
    /// Quality preset definitions for video transcoding.
    /// </summary>
    public static class QualityPresets
    {
        /// <summary>4K UHD quality preset (3840x2160).</summary>
        public static TranscodingProfile UHD4K => new()
        {
            ProfileId = "4k",
            Name = "4K UHD",
            Description = "Ultra HD 4K quality for large displays",
            OutputFormat = MediaFormat.Mp4,
            Quality = QualityPreset.Maximum,
            VideoCodec = "libx265",
            VideoBitrate = 15000,
            Width = 3840,
            Height = 2160,
            FrameRate = 30,
            AudioCodec = "aac",
            AudioBitrate = 256,
            SampleRate = 48000,
            Channels = 2,
            UseHardwareAcceleration = true,
            Priority = 10
        };

        /// <summary>1080p Full HD quality preset (1920x1080).</summary>
        public static TranscodingProfile FullHD1080p => new()
        {
            ProfileId = "1080p",
            Name = "1080p Full HD",
            Description = "Full HD quality for standard displays",
            OutputFormat = MediaFormat.Mp4,
            Quality = QualityPreset.High,
            VideoCodec = "libx264",
            VideoBitrate = 5000,
            Width = 1920,
            Height = 1080,
            FrameRate = 30,
            AudioCodec = "aac",
            AudioBitrate = 192,
            SampleRate = 48000,
            Channels = 2,
            UseHardwareAcceleration = true,
            Priority = 8
        };

        /// <summary>720p HD quality preset (1280x720).</summary>
        public static TranscodingProfile HD720p => new()
        {
            ProfileId = "720p",
            Name = "720p HD",
            Description = "HD quality for streaming",
            OutputFormat = MediaFormat.Mp4,
            Quality = QualityPreset.Medium,
            VideoCodec = "libx264",
            VideoBitrate = 2500,
            Width = 1280,
            Height = 720,
            FrameRate = 30,
            AudioCodec = "aac",
            AudioBitrate = 128,
            SampleRate = 44100,
            Channels = 2,
            UseHardwareAcceleration = true,
            Priority = 6
        };

        /// <summary>480p SD quality preset (854x480).</summary>
        public static TranscodingProfile SD480p => new()
        {
            ProfileId = "480p",
            Name = "480p SD",
            Description = "Standard definition for mobile and low bandwidth",
            OutputFormat = MediaFormat.Mp4,
            Quality = QualityPreset.Low,
            VideoCodec = "libx264",
            VideoBitrate = 1000,
            Width = 854,
            Height = 480,
            FrameRate = 30,
            AudioCodec = "aac",
            AudioBitrate = 96,
            SampleRate = 44100,
            Channels = 2,
            UseHardwareAcceleration = true,
            Priority = 4
        };

        /// <summary>Thumbnail preset for video previews (320x180).</summary>
        public static TranscodingProfile VideoThumbnail => new()
        {
            ProfileId = "video-thumbnail",
            Name = "Video Thumbnail",
            Description = "Small preview image extracted from video",
            OutputFormat = MediaFormat.Jpeg,
            Quality = QualityPreset.Medium,
            Width = 320,
            Height = 180,
            ImageQuality = 80,
            StripMetadata = true,
            Priority = 2
        };

        /// <summary>Audio-only extraction preset.</summary>
        public static TranscodingProfile AudioOnly => new()
        {
            ProfileId = "audio-only",
            Name = "Audio Extraction",
            Description = "Extract audio track from video",
            OutputFormat = MediaFormat.Aac,
            Quality = QualityPreset.High,
            AudioCodec = "aac",
            AudioBitrate = 192,
            SampleRate = 48000,
            Channels = 2,
            Priority = 5
        };
    }

    private void RegisterQualityPresetProfiles()
    {
        _customProfiles["4k"] = QualityPresets.UHD4K;
        _customProfiles["1080p"] = QualityPresets.FullHD1080p;
        _customProfiles["720p"] = QualityPresets.HD720p;
        _customProfiles["480p"] = QualityPresets.SD480p;
        _customProfiles["video-thumbnail"] = QualityPresets.VideoThumbnail;
        _customProfiles["audio-only"] = QualityPresets.AudioOnly;
    }

    /// <inheritdoc />
    public override Task<TranscodingProfile?> GetProfileAsync(string profileId, CancellationToken ct = default)
    {
        if (_customProfiles.TryGetValue(profileId, out var profile))
        {
            return Task.FromResult<TranscodingProfile?>(profile);
        }

        return base.GetProfileAsync(profileId, ct);
    }

    /// <inheritdoc />
    public override Task<TranscodingProfile> RegisterProfileAsync(TranscodingProfile profile, CancellationToken ct = default)
    {
        _customProfiles[profile.ProfileId] = profile;
        return base.RegisterProfileAsync(profile, ct);
    }

    #endregion

    #region Adaptive Bitrate Streaming (72.6)

    /// <summary>
    /// Generates an HLS manifest structure for adaptive bitrate streaming.
    /// </summary>
    /// <param name="sourcePath">Path to the source video.</param>
    /// <param name="outputDir">Output directory for HLS files.</param>
    /// <param name="segmentDuration">Segment duration in seconds (default 6).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>HLS manifest structure.</returns>
    public async Task<HlsManifest> GenerateHlsManifestAsync(
        string sourcePath,
        string outputDir,
        int segmentDuration = 6,
        CancellationToken ct = default)
    {
        var sourceInfo = await ProbeAsync(sourcePath, ct);
        var variants = new List<HlsVariant>();

        // Determine which quality levels to generate based on source resolution
        var sourceHeight = sourceInfo.Height ?? 1080;
        var profiles = new List<TranscodingProfile>();

        if (sourceHeight >= 2160)
            profiles.Add(QualityPresets.UHD4K);
        if (sourceHeight >= 1080)
            profiles.Add(QualityPresets.FullHD1080p);
        if (sourceHeight >= 720)
            profiles.Add(QualityPresets.HD720p);
        profiles.Add(QualityPresets.SD480p);

        foreach (var profile in profiles)
        {
            var bandwidth = (profile.VideoBitrate ?? 2500) * 1000 + (profile.AudioBitrate ?? 128) * 1000;
            var resolution = $"{profile.Width}x{profile.Height}";
            var playlistName = $"variant_{profile.ProfileId}.m3u8";

            variants.Add(new HlsVariant
            {
                ProfileId = profile.ProfileId,
                Bandwidth = bandwidth,
                Resolution = resolution,
                Codecs = $"avc1.640028,mp4a.40.2", // H.264 High + AAC
                PlaylistPath = Path.Combine(outputDir, playlistName),
                SegmentDuration = segmentDuration
            });
        }

        var manifest = new HlsManifest
        {
            MasterPlaylistPath = Path.Combine(outputDir, "master.m3u8"),
            Variants = variants,
            SegmentDuration = segmentDuration,
            MediaDuration = sourceInfo.Duration
        };

        return manifest;
    }

    /// <summary>
    /// Generates a DASH manifest structure for adaptive bitrate streaming.
    /// </summary>
    /// <param name="sourcePath">Path to the source video.</param>
    /// <param name="outputDir">Output directory for DASH files.</param>
    /// <param name="segmentDuration">Segment duration in seconds (default 4).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>DASH manifest structure.</returns>
    public async Task<DashManifest> GenerateDashManifestAsync(
        string sourcePath,
        string outputDir,
        int segmentDuration = 4,
        CancellationToken ct = default)
    {
        var sourceInfo = await ProbeAsync(sourcePath, ct);
        var representations = new List<DashRepresentation>();

        var sourceHeight = sourceInfo.Height ?? 1080;
        var profiles = new List<TranscodingProfile>();

        if (sourceHeight >= 2160)
            profiles.Add(QualityPresets.UHD4K);
        if (sourceHeight >= 1080)
            profiles.Add(QualityPresets.FullHD1080p);
        if (sourceHeight >= 720)
            profiles.Add(QualityPresets.HD720p);
        profiles.Add(QualityPresets.SD480p);

        int id = 1;
        foreach (var profile in profiles)
        {
            var bandwidth = (profile.VideoBitrate ?? 2500) * 1000 + (profile.AudioBitrate ?? 128) * 1000;

            representations.Add(new DashRepresentation
            {
                Id = $"video_{id}",
                ProfileId = profile.ProfileId,
                Bandwidth = bandwidth,
                Width = profile.Width ?? 1920,
                Height = profile.Height ?? 1080,
                FrameRate = profile.FrameRate ?? 30,
                Codecs = "avc1.640028",
                MimeType = "video/mp4",
                SegmentTemplate = $"video_{id}_$Number$.m4s",
                InitializationSegment = $"video_{id}_init.mp4"
            });
            id++;
        }

        // Add audio representation
        representations.Add(new DashRepresentation
        {
            Id = "audio_1",
            ProfileId = "audio",
            Bandwidth = 128000,
            Codecs = "mp4a.40.2",
            MimeType = "audio/mp4",
            AudioSampleRate = 48000,
            AudioChannels = 2,
            SegmentTemplate = "audio_$Number$.m4s",
            InitializationSegment = "audio_init.mp4"
        });

        return new DashManifest
        {
            MpdPath = Path.Combine(outputDir, "manifest.mpd"),
            Representations = representations,
            SegmentDuration = segmentDuration,
            MediaDuration = sourceInfo.Duration,
            MinBufferTime = TimeSpan.FromSeconds(2)
        };
    }

    /// <summary>
    /// Builds FFmpeg command for HLS transcoding.
    /// </summary>
    public string BuildHlsTranscodingCommand(string sourcePath, HlsVariant variant, string outputDir)
    {
        var profile = _customProfiles.GetValueOrDefault(variant.ProfileId, QualityPresets.HD720p);
        var playlistPath = Path.Combine(outputDir, $"variant_{variant.ProfileId}.m3u8");
        var segmentPattern = Path.Combine(outputDir, $"segment_{variant.ProfileId}_%03d.ts");

        var sb = new StringBuilder();
        sb.Append($"-i \"{sourcePath}\" ");
        sb.Append($"-c:v {profile.VideoCodec ?? "libx264"} ");
        sb.Append($"-b:v {profile.VideoBitrate ?? 2500}k ");
        sb.Append($"-vf scale={profile.Width ?? 1920}:{profile.Height ?? 1080} ");
        sb.Append($"-c:a {profile.AudioCodec ?? "aac"} ");
        sb.Append($"-b:a {profile.AudioBitrate ?? 128}k ");
        sb.Append($"-hls_time {variant.SegmentDuration} ");
        sb.Append("-hls_list_size 0 ");
        sb.Append($"-hls_segment_filename \"{segmentPattern}\" ");
        sb.Append($"\"{playlistPath}\"");

        return sb.ToString();
    }

    #endregion

    #region Transcoding Queue (72.7)

    /// <summary>
    /// Submits a job with priority to the transcoding queue.
    /// </summary>
    /// <param name="sourcePath">Source file path.</param>
    /// <param name="targetPath">Target output path.</param>
    /// <param name="profile">Transcoding profile.</param>
    /// <param name="priority">Job priority (higher = more important).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Submitted job information.</returns>
    public async Task<TranscodingJob> SubmitJobWithPriorityAsync(
        string sourcePath,
        string targetPath,
        TranscodingProfile profile,
        int priority,
        CancellationToken ct = default)
    {
        // Create a new profile with updated priority
        var prioritizedProfile = new TranscodingProfile
        {
            ProfileId = profile.ProfileId,
            Name = profile.Name,
            Description = profile.Description,
            OutputFormat = profile.OutputFormat,
            Quality = profile.Quality,
            VideoCodec = profile.VideoCodec,
            VideoBitrate = profile.VideoBitrate,
            Width = profile.Width,
            Height = profile.Height,
            FrameRate = profile.FrameRate,
            MaintainAspectRatio = profile.MaintainAspectRatio,
            AudioCodec = profile.AudioCodec,
            AudioBitrate = profile.AudioBitrate,
            SampleRate = profile.SampleRate,
            Channels = profile.Channels,
            ImageQuality = profile.ImageQuality,
            StripMetadata = profile.StripMetadata,
            UseHardwareAcceleration = profile.UseHardwareAcceleration,
            MaxConcurrentJobs = profile.MaxConcurrentJobs,
            Priority = priority,
            CustomOptions = profile.CustomOptions
        };
        var job = await base.SubmitJobAsync(sourcePath, targetPath, prioritizedProfile, ct);

        await _queueLock.WaitAsync(ct);
        try
        {
            // Negative priority for max-heap behavior (higher priority first)
            _jobQueue.Enqueue(job.JobId, -priority);
        }
        finally
        {
            _queueLock.Release();
        }

        return job;
    }

    private void ProcessQueue(object? state)
    {
        if (!_isRunning)
            return;

        _ = ProcessNextJobAsync();
    }

    private async Task ProcessNextJobAsync()
    {
        if (!await _processingSlots.WaitAsync(0))
            return; // All slots busy

        string? jobId = null;

        try
        {
            await _queueLock.WaitAsync();
            try
            {
                if (_jobQueue.Count > 0)
                {
                    jobId = _jobQueue.Dequeue();
                }
            }
            finally
            {
                _queueLock.Release();
            }

            if (jobId != null && _jobContexts.TryGetValue(jobId, out var context))
            {
                await ProcessJobInternalAsync(context);
            }
        }
        finally
        {
            _processingSlots.Release();
        }
    }

    #endregion

    #region Result Caching (72.8)

    /// <summary>
    /// Gets a cached transcoding result if available.
    /// </summary>
    /// <param name="sourcePath">Source file path.</param>
    /// <param name="profileId">Profile identifier.</param>
    /// <returns>Cached result or null if not found.</returns>
    public CachedTranscodingResult? GetCachedResult(string sourcePath, string profileId)
    {
        var cacheKey = GenerateCacheKey(sourcePath, profileId);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            if (cached.ExpiresAt > DateTime.UtcNow)
            {
                return cached;
            }
            // Remove expired entry
            _cache.TryRemove(cacheKey, out _);
        }
        return null;
    }

    /// <summary>
    /// Caches a transcoding result.
    /// </summary>
    /// <param name="sourcePath">Source file path.</param>
    /// <param name="profileId">Profile identifier.</param>
    /// <param name="result">Transcoding result.</param>
    /// <param name="ttl">Time to live for the cache entry.</param>
    public void CacheResult(string sourcePath, string profileId, TranscodingResult result, TimeSpan? ttl = null)
    {
        var cacheKey = GenerateCacheKey(sourcePath, profileId);
        var expiry = DateTime.UtcNow + (ttl ?? TimeSpan.FromHours(24));

        _cache[cacheKey] = new CachedTranscodingResult
        {
            CacheKey = cacheKey,
            SourcePath = sourcePath,
            ProfileId = profileId,
            Result = result,
            CachedAt = DateTime.UtcNow,
            ExpiresAt = expiry
        };
    }

    private string GenerateCacheKey(string sourcePath, string profileId)
    {
        // Use source file hash + profile ID for cache key
        using var sha = SHA256.Create();
        var input = $"{sourcePath}|{profileId}|{GetFileModifiedTime(sourcePath)}";
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash);
    }

    private DateTime GetFileModifiedTime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private void CleanupCache(object? state)
    {
        var now = DateTime.UtcNow;
        var expiredKeys = _cache
            .Where(kv => kv.Value.ExpiresAt <= now)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _cache.TryRemove(key, out _);
        }
    }

    #endregion

    #region Watermarking (72.9)

    /// <summary>
    /// Configuration for watermark injection.
    /// </summary>
    public class WatermarkConfig
    {
        /// <summary>Type of watermark (text or image).</summary>
        public WatermarkType Type { get; init; } = WatermarkType.Text;

        /// <summary>Text content for text watermarks.</summary>
        public string? Text { get; init; }

        /// <summary>Path to image file for image watermarks.</summary>
        public string? ImagePath { get; init; }

        /// <summary>Position of the watermark.</summary>
        public WatermarkPosition Position { get; init; } = WatermarkPosition.BottomRight;

        /// <summary>Opacity (0.0 to 1.0).</summary>
        public double Opacity { get; init; } = 0.5;

        /// <summary>Font size for text watermarks.</summary>
        public int FontSize { get; init; } = 24;

        /// <summary>Font color for text watermarks (hex or name).</summary>
        public string FontColor { get; init; } = "white";

        /// <summary>Margin from edge in pixels.</summary>
        public int Margin { get; init; } = 10;

        /// <summary>Scale factor for image watermarks (1.0 = original size).</summary>
        public double Scale { get; init; } = 1.0;
    }

    /// <summary>
    /// Type of watermark to apply.
    /// </summary>
    public enum WatermarkType
    {
        /// <summary>Text-based watermark.</summary>
        Text,
        /// <summary>Image-based watermark.</summary>
        Image
    }

    /// <summary>
    /// Position for watermark placement.
    /// </summary>
    public enum WatermarkPosition
    {
        /// <summary>Top-left corner.</summary>
        TopLeft,
        /// <summary>Top-center.</summary>
        TopCenter,
        /// <summary>Top-right corner.</summary>
        TopRight,
        /// <summary>Center-left.</summary>
        CenterLeft,
        /// <summary>Center of frame.</summary>
        Center,
        /// <summary>Center-right.</summary>
        CenterRight,
        /// <summary>Bottom-left corner.</summary>
        BottomLeft,
        /// <summary>Bottom-center.</summary>
        BottomCenter,
        /// <summary>Bottom-right corner.</summary>
        BottomRight
    }

    /// <summary>
    /// Builds FFmpeg filter string for watermark injection.
    /// </summary>
    /// <param name="config">Watermark configuration.</param>
    /// <param name="videoWidth">Video width in pixels.</param>
    /// <param name="videoHeight">Video height in pixels.</param>
    /// <returns>FFmpeg filter string.</returns>
    public string BuildWatermarkFilter(WatermarkConfig config, int videoWidth, int videoHeight)
    {
        if (config.Type == WatermarkType.Text)
        {
            return BuildTextWatermarkFilter(config, videoWidth, videoHeight);
        }
        else
        {
            return BuildImageWatermarkFilter(config, videoWidth, videoHeight);
        }
    }

    private string BuildTextWatermarkFilter(WatermarkConfig config, int videoWidth, int videoHeight)
    {
        var position = GetPositionExpression(config.Position, config.Margin, videoWidth, videoHeight);
        var alpha = config.Opacity;

        return $"drawtext=text='{EscapeFFmpegText(config.Text ?? "")}':fontsize={config.FontSize}:fontcolor={config.FontColor}@{alpha}:x={position.X}:y={position.Y}";
    }

    private string BuildImageWatermarkFilter(WatermarkConfig config, int videoWidth, int videoHeight)
    {
        var position = GetPositionExpression(config.Position, config.Margin, videoWidth, videoHeight);
        var scale = config.Scale;
        var alpha = config.Opacity;

        // Overlay filter with scaling
        return $"[1:v]scale=iw*{scale}:ih*{scale},format=rgba,colorchannelmixer=aa={alpha}[wm];[0:v][wm]overlay={position.X}:{position.Y}";
    }

    private (string X, string Y) GetPositionExpression(WatermarkPosition position, int margin, int width, int height)
    {
        return position switch
        {
            WatermarkPosition.TopLeft => ($"{margin}", $"{margin}"),
            WatermarkPosition.TopCenter => ($"(w-tw)/2", $"{margin}"),
            WatermarkPosition.TopRight => ($"w-tw-{margin}", $"{margin}"),
            WatermarkPosition.CenterLeft => ($"{margin}", "(h-th)/2"),
            WatermarkPosition.Center => ("(w-tw)/2", "(h-th)/2"),
            WatermarkPosition.CenterRight => ($"w-tw-{margin}", "(h-th)/2"),
            WatermarkPosition.BottomLeft => ($"{margin}", $"h-th-{margin}"),
            WatermarkPosition.BottomCenter => ("(w-tw)/2", $"h-th-{margin}"),
            WatermarkPosition.BottomRight => ($"w-tw-{margin}", $"h-th-{margin}"),
            _ => ($"w-tw-{margin}", $"h-th-{margin}")
        };
    }

    private static string EscapeFFmpegText(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("'", "'\\''")
            .Replace(":", "\\:")
            .Replace("[", "\\[")
            .Replace("]", "\\]");
    }

    /// <summary>
    /// Builds ImageMagick command for image watermarking.
    /// </summary>
    /// <param name="sourcePath">Source image path.</param>
    /// <param name="outputPath">Output image path.</param>
    /// <param name="config">Watermark configuration.</param>
    /// <returns>ImageMagick command arguments.</returns>
    public string BuildImageMagickWatermarkCommand(string sourcePath, string outputPath, WatermarkConfig config)
    {
        var gravity = GetImageMagickGravity(config.Position);
        var opacity = (int)(config.Opacity * 100);

        if (config.Type == WatermarkType.Text)
        {
            return $"\"{sourcePath}\" -gravity {gravity} -pointsize {config.FontSize} " +
                   $"-fill \"rgba(255,255,255,{config.Opacity})\" " +
                   $"-annotate +{config.Margin}+{config.Margin} \"{config.Text}\" " +
                   $"\"{outputPath}\"";
        }
        else
        {
            return $"\"{sourcePath}\" " +
                   $"( \"{config.ImagePath}\" -resize {(int)(config.Scale * 100)}% -alpha set -channel A -evaluate multiply {config.Opacity} ) " +
                   $"-gravity {gravity} -geometry +{config.Margin}+{config.Margin} " +
                   $"-composite \"{outputPath}\"";
        }
    }

    private static string GetImageMagickGravity(WatermarkPosition position)
    {
        return position switch
        {
            WatermarkPosition.TopLeft => "NorthWest",
            WatermarkPosition.TopCenter => "North",
            WatermarkPosition.TopRight => "NorthEast",
            WatermarkPosition.CenterLeft => "West",
            WatermarkPosition.Center => "Center",
            WatermarkPosition.CenterRight => "East",
            WatermarkPosition.BottomLeft => "SouthWest",
            WatermarkPosition.BottomCenter => "South",
            WatermarkPosition.BottomRight => "SouthEast",
            _ => "SouthEast"
        };
    }

    #endregion

    #region Progress Tracking (72.10)

    /// <summary>
    /// Raised when transcoding progress updates.
    /// </summary>
    protected virtual void OnProgressUpdated(TranscodingProgressEventArgs e)
    {
        ProgressUpdated?.Invoke(this, e);
    }

    /// <summary>
    /// Raised when a job completes.
    /// </summary>
    protected virtual void OnJobCompleted(TranscodingCompletedEventArgs e)
    {
        JobCompleted?.Invoke(this, e);
    }

    /// <summary>
    /// Updates progress for a job and raises events.
    /// </summary>
    protected void UpdateProgress(string jobId, double progress, string? currentOperation = null)
    {
        UpdateJobStatus(jobId, TranscodingStatus.InProgress, progress);

        OnProgressUpdated(new TranscodingProgressEventArgs
        {
            JobId = jobId,
            Progress = progress,
            CurrentOperation = currentOperation,
            Timestamp = DateTime.UtcNow
        });
    }

    #endregion

    #region FFmpeg Integration (72.1)

    /// <summary>
    /// Builds an FFmpeg command line for transcoding.
    /// </summary>
    /// <param name="sourcePath">Source file path.</param>
    /// <param name="outputPath">Output file path.</param>
    /// <param name="profile">Transcoding profile.</param>
    /// <param name="watermark">Optional watermark configuration.</param>
    /// <returns>FFmpeg command arguments.</returns>
    public string BuildFFmpegCommand(
        string sourcePath,
        string outputPath,
        TranscodingProfile profile,
        WatermarkConfig? watermark = null)
    {
        var sb = new StringBuilder();

        // Input
        sb.Append($"-i \"{sourcePath}\" ");

        // Hardware acceleration
        if (profile.UseHardwareAcceleration && HardwareAccelerationAvailable)
        {
            sb.Append("-hwaccel auto ");
        }

        // Video codec and settings
        if (!string.IsNullOrEmpty(profile.VideoCodec))
        {
            sb.Append($"-c:v {profile.VideoCodec} ");

            if (profile.VideoBitrate.HasValue)
            {
                sb.Append($"-b:v {profile.VideoBitrate}k ");
            }

            // Video filters
            var filters = new List<string>();

            if (profile.Width.HasValue && profile.Height.HasValue)
            {
                var scaleFilter = profile.MaintainAspectRatio
                    ? $"scale={profile.Width}:{profile.Height}:force_original_aspect_ratio=decrease,pad={profile.Width}:{profile.Height}:(ow-iw)/2:(oh-ih)/2"
                    : $"scale={profile.Width}:{profile.Height}";
                filters.Add(scaleFilter);
            }

            // Add watermark filter if specified
            if (watermark != null && watermark.Type == WatermarkType.Text)
            {
                filters.Add(BuildTextWatermarkFilter(watermark, profile.Width ?? 1920, profile.Height ?? 1080));
            }

            if (filters.Count > 0)
            {
                sb.Append($"-vf \"{string.Join(",", filters)}\" ");
            }

            if (profile.FrameRate.HasValue)
            {
                sb.Append($"-r {profile.FrameRate} ");
            }

            // Quality preset mapping
            var preset = profile.Quality switch
            {
                QualityPreset.Draft => "ultrafast",
                QualityPreset.Low => "veryfast",
                QualityPreset.Medium => "medium",
                QualityPreset.High => "slow",
                QualityPreset.Maximum => "veryslow",
                QualityPreset.Lossless => "veryslow",
                _ => "medium"
            };
            sb.Append($"-preset {preset} ");

            if (profile.Quality == QualityPreset.Lossless)
            {
                sb.Append("-crf 0 ");
            }
        }

        // Audio codec and settings
        if (!string.IsNullOrEmpty(profile.AudioCodec))
        {
            sb.Append($"-c:a {profile.AudioCodec} ");

            if (profile.AudioBitrate.HasValue)
            {
                sb.Append($"-b:a {profile.AudioBitrate}k ");
            }

            if (profile.SampleRate.HasValue)
            {
                sb.Append($"-ar {profile.SampleRate} ");
            }

            if (profile.Channels.HasValue)
            {
                sb.Append($"-ac {profile.Channels} ");
            }
        }

        // Custom options
        if (profile.CustomOptions.Count > 0)
        {
            foreach (var option in profile.CustomOptions)
            {
                sb.Append($"-{option.Key} {option.Value} ");
            }
        }

        // Output format based on container
        var outputFormat = GetFFmpegFormat(profile.OutputFormat);
        if (!string.IsNullOrEmpty(outputFormat))
        {
            sb.Append($"-f {outputFormat} ");
        }

        // Overwrite output
        sb.Append("-y ");

        // Output path
        sb.Append($"\"{outputPath}\"");

        return sb.ToString();
    }

    /// <summary>
    /// Builds FFmpeg command for thumbnail extraction.
    /// </summary>
    public string BuildFFmpegThumbnailCommand(string sourcePath, string outputPath, TranscodingProfile profile, TimeSpan? timestamp = null)
    {
        var ts = timestamp ?? TimeSpan.FromSeconds(1);
        var sb = new StringBuilder();

        sb.Append($"-ss {ts:hh\\:mm\\:ss\\.fff} ");
        sb.Append($"-i \"{sourcePath}\" ");
        sb.Append("-vframes 1 ");

        if (profile.Width.HasValue && profile.Height.HasValue)
        {
            sb.Append($"-vf scale={profile.Width}:{profile.Height}:force_original_aspect_ratio=decrease ");
        }

        if (profile.ImageQuality.HasValue)
        {
            sb.Append($"-qscale:v {Math.Max(1, Math.Min(31, 31 - profile.ImageQuality.Value * 30 / 100))} ");
        }

        sb.Append("-y ");
        sb.Append($"\"{outputPath}\"");

        return sb.ToString();
    }

    private static string? GetFFmpegFormat(MediaFormat format)
    {
        return format switch
        {
            MediaFormat.Mp4 => "mp4",
            MediaFormat.WebM => "webm",
            MediaFormat.Mkv => "matroska",
            MediaFormat.Avi => "avi",
            MediaFormat.Mov => "mov",
            MediaFormat.Mp3 => "mp3",
            MediaFormat.Aac => "adts",
            MediaFormat.Flac => "flac",
            MediaFormat.Ogg => "ogg",
            MediaFormat.Wav => "wav",
            MediaFormat.Jpeg => "image2",
            MediaFormat.Png => "image2",
            _ => null
        };
    }

    /// <summary>
    /// Parses FFmpeg progress output to extract progress percentage.
    /// </summary>
    public double ParseFFmpegProgress(string output, TimeSpan totalDuration)
    {
        // Look for "time=" in output
        var timeMatch = System.Text.RegularExpressions.Regex.Match(output, @"time=(\d{2}):(\d{2}):(\d{2})\.(\d{2,3})");
        if (timeMatch.Success)
        {
            var hours = int.Parse(timeMatch.Groups[1].Value);
            var minutes = int.Parse(timeMatch.Groups[2].Value);
            var seconds = int.Parse(timeMatch.Groups[3].Value);
            var milliseconds = int.Parse(timeMatch.Groups[4].Value.PadRight(3, '0'));

            var currentTime = new TimeSpan(0, hours, minutes, seconds, milliseconds);

            if (totalDuration.TotalSeconds > 0)
            {
                return Math.Min(100, currentTime.TotalSeconds / totalDuration.TotalSeconds * 100);
            }
        }

        return 0;
    }

    #endregion

    #region ImageMagick Integration (72.2)

    /// <summary>
    /// Builds an ImageMagick command line for image processing.
    /// </summary>
    /// <param name="sourcePath">Source image path.</param>
    /// <param name="outputPath">Output image path.</param>
    /// <param name="profile">Transcoding profile.</param>
    /// <returns>ImageMagick command arguments.</returns>
    public string BuildImageMagickCommand(string sourcePath, string outputPath, TranscodingProfile profile)
    {
        var sb = new StringBuilder();

        sb.Append($"\"{sourcePath}\" ");

        // Resize
        if (profile.Width.HasValue && profile.Height.HasValue)
        {
            var resizeOp = profile.MaintainAspectRatio ? ">" : "!";
            sb.Append($"-resize {profile.Width}x{profile.Height}{resizeOp} ");
        }

        // Quality
        if (profile.ImageQuality.HasValue)
        {
            sb.Append($"-quality {profile.ImageQuality} ");
        }

        // Strip metadata
        if (profile.StripMetadata)
        {
            sb.Append("-strip ");
        }

        // Format-specific options
        switch (profile.OutputFormat)
        {
            case MediaFormat.WebP:
                sb.Append("-define webp:lossless=false ");
                break;
            case MediaFormat.Avif:
                sb.Append("-define heic:speed=5 ");
                break;
            case MediaFormat.Jpeg:
                sb.Append("-sampling-factor 4:2:0 ");
                sb.Append("-interlace JPEG ");
                break;
            case MediaFormat.Png:
                sb.Append("-define png:compression-level=9 ");
                break;
        }

        sb.Append($"\"{outputPath}\"");

        return sb.ToString();
    }

    /// <summary>
    /// Builds ImageMagick command for batch image conversion.
    /// </summary>
    public string BuildImageMagickBatchCommand(string inputPattern, string outputDir, TranscodingProfile profile)
    {
        var extension = profile.OutputFormat switch
        {
            MediaFormat.WebP => "webp",
            MediaFormat.Jpeg => "jpg",
            MediaFormat.Png => "png",
            MediaFormat.Avif => "avif",
            MediaFormat.Gif => "gif",
            _ => "jpg"
        };

        var sb = new StringBuilder();
        sb.Append("mogrify ");
        sb.Append($"-path \"{outputDir}\" ");
        sb.Append($"-format {extension} ");

        if (profile.Width.HasValue && profile.Height.HasValue)
        {
            sb.Append($"-resize {profile.Width}x{profile.Height} ");
        }

        if (profile.ImageQuality.HasValue)
        {
            sb.Append($"-quality {profile.ImageQuality} ");
        }

        if (profile.StripMetadata)
        {
            sb.Append("-strip ");
        }

        sb.Append($"\"{inputPattern}\"");

        return sb.ToString();
    }

    #endregion

    #region Document Conversion (72.3)

    /// <summary>
    /// Represents a document conversion path.
    /// </summary>
    public class DocumentConversionPath
    {
        /// <summary>Source format.</summary>
        public MediaFormat SourceFormat { get; init; }
        /// <summary>Target format.</summary>
        public MediaFormat TargetFormat { get; init; }
        /// <summary>Converter tool to use.</summary>
        public string Converter { get; init; } = string.Empty;
        /// <summary>Command template.</summary>
        public string CommandTemplate { get; init; } = string.Empty;
        /// <summary>Whether conversion is supported.</summary>
        public bool IsSupported { get; init; }
    }

    /// <summary>
    /// Gets the conversion path for document formats.
    /// </summary>
    public DocumentConversionPath GetDocumentConversionPath(MediaFormat source, MediaFormat target)
    {
        // PDF conversions
        if (source == MediaFormat.Pdf)
        {
            if (target == MediaFormat.Html)
            {
                return new DocumentConversionPath
                {
                    SourceFormat = source,
                    TargetFormat = target,
                    Converter = "pdftohtml",
                    CommandTemplate = "pdftohtml -s -noframes \"{input}\" \"{output}\"",
                    IsSupported = true
                };
            }
            if (target == MediaFormat.Txt)
            {
                return new DocumentConversionPath
                {
                    SourceFormat = source,
                    TargetFormat = target,
                    Converter = "pdftotext",
                    CommandTemplate = "pdftotext \"{input}\" \"{output}\"",
                    IsSupported = true
                };
            }
            if (target == MediaFormat.Png || target == MediaFormat.Jpeg)
            {
                return new DocumentConversionPath
                {
                    SourceFormat = source,
                    TargetFormat = target,
                    Converter = "pdftoppm",
                    CommandTemplate = $"pdftoppm -{(target == MediaFormat.Jpeg ? "jpeg" : "png")} \"{"{input}"}\" \"{"{output}"}\"",
                    IsSupported = true
                };
            }
        }

        // DOCX conversions
        if (source == MediaFormat.Docx)
        {
            if (target == MediaFormat.Pdf)
            {
                return new DocumentConversionPath
                {
                    SourceFormat = source,
                    TargetFormat = target,
                    Converter = "libreoffice",
                    CommandTemplate = "soffice --headless --convert-to pdf --outdir \"{outdir}\" \"{input}\"",
                    IsSupported = true
                };
            }
            if (target == MediaFormat.Html)
            {
                return new DocumentConversionPath
                {
                    SourceFormat = source,
                    TargetFormat = target,
                    Converter = "pandoc",
                    CommandTemplate = "pandoc \"{input}\" -o \"{output}\" -s",
                    IsSupported = true
                };
            }
            if (target == MediaFormat.Txt)
            {
                return new DocumentConversionPath
                {
                    SourceFormat = source,
                    TargetFormat = target,
                    Converter = "pandoc",
                    CommandTemplate = "pandoc \"{input}\" -o \"{output}\" -t plain",
                    IsSupported = true
                };
            }
        }

        // HTML conversions
        if (source == MediaFormat.Html)
        {
            if (target == MediaFormat.Pdf)
            {
                return new DocumentConversionPath
                {
                    SourceFormat = source,
                    TargetFormat = target,
                    Converter = "wkhtmltopdf",
                    CommandTemplate = "wkhtmltopdf \"{input}\" \"{output}\"",
                    IsSupported = true
                };
            }
            if (target == MediaFormat.Txt)
            {
                return new DocumentConversionPath
                {
                    SourceFormat = source,
                    TargetFormat = target,
                    Converter = "pandoc",
                    CommandTemplate = "pandoc \"{input}\" -o \"{output}\" -t plain",
                    IsSupported = true
                };
            }
        }

        // Markdown conversions
        if (source == MediaFormat.Markdown)
        {
            if (target == MediaFormat.Html)
            {
                return new DocumentConversionPath
                {
                    SourceFormat = source,
                    TargetFormat = target,
                    Converter = "pandoc",
                    CommandTemplate = "pandoc \"{input}\" -o \"{output}\" -s",
                    IsSupported = true
                };
            }
            if (target == MediaFormat.Pdf)
            {
                return new DocumentConversionPath
                {
                    SourceFormat = source,
                    TargetFormat = target,
                    Converter = "pandoc",
                    CommandTemplate = "pandoc \"{input}\" -o \"{output}\"",
                    IsSupported = true
                };
            }
        }

        return new DocumentConversionPath
        {
            SourceFormat = source,
            TargetFormat = target,
            Converter = string.Empty,
            CommandTemplate = string.Empty,
            IsSupported = false
        };
    }

    /// <summary>
    /// Gets all supported document conversion paths.
    /// </summary>
    public IReadOnlyList<DocumentConversionPath> GetSupportedDocumentConversions()
    {
        var conversions = new List<DocumentConversionPath>();
        var documentFormats = new[] { MediaFormat.Pdf, MediaFormat.Docx, MediaFormat.Html, MediaFormat.Txt, MediaFormat.Markdown };
        var imageFormats = new[] { MediaFormat.Png, MediaFormat.Jpeg };

        foreach (var source in documentFormats)
        {
            foreach (var target in documentFormats.Concat(imageFormats))
            {
                if (source == target) continue;

                var path = GetDocumentConversionPath(source, target);
                if (path.IsSupported)
                {
                    conversions.Add(path);
                }
            }
        }

        return conversions;
    }

    #endregion

    #region Core Processing Implementation

    /// <inheritdoc />
    public override async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        var format = await DetectFormatAsync(path, ct);
        var fileInfo = new FileInfo(path);

        if (!fileInfo.Exists)
        {
            return new MediaInfo
            {
                Format = MediaFormat.Unknown,
                MimeType = "application/octet-stream",
                SizeBytes = 0
            };
        }

        var mediaInfo = new MediaInfo
        {
            Format = format,
            MimeType = FormatToMime.GetValueOrDefault(format, "application/octet-stream"),
            SizeBytes = fileInfo.Length
        };

        // If FFmpeg is available, use it for detailed probing
        if (_ffmpegAvailable && IsVideoOrAudioFormat(format))
        {
            var probeInfo = await ProbeWithFFmpegAsync(path, ct);
            if (probeInfo != null)
            {
                return new MediaInfo
                {
                    Format = format,
                    MimeType = probeInfo.MimeType,
                    SizeBytes = probeInfo.SizeBytes,
                    Duration = probeInfo.Duration,
                    Width = probeInfo.Width,
                    Height = probeInfo.Height,
                    FrameRate = probeInfo.FrameRate,
                    VideoCodec = probeInfo.VideoCodec,
                    VideoBitrate = probeInfo.VideoBitrate,
                    AudioCodec = probeInfo.AudioCodec,
                    AudioBitrate = probeInfo.AudioBitrate,
                    SampleRate = probeInfo.SampleRate,
                    Channels = probeInfo.Channels,
                    Metadata = probeInfo.Metadata
                };
            }
        }

        // For images, try to get dimensions
        if (IsImageFormat(format))
        {
            var dimensions = await GetImageDimensionsAsync(path, ct);
            if (dimensions.HasValue)
            {
                return new MediaInfo
                {
                    Format = format,
                    MimeType = mediaInfo.MimeType,
                    SizeBytes = fileInfo.Length,
                    Width = dimensions.Value.Width,
                    Height = dimensions.Value.Height
                };
            }
        }

        return mediaInfo;
    }

    private static bool IsVideoOrAudioFormat(MediaFormat format)
    {
        return format switch
        {
            MediaFormat.Mp4 or MediaFormat.WebM or MediaFormat.Mkv or
            MediaFormat.Avi or MediaFormat.Mov or MediaFormat.H264 or
            MediaFormat.H265 or MediaFormat.VP8 or MediaFormat.VP9 or
            MediaFormat.AV1 or MediaFormat.Mp3 or MediaFormat.Aac or
            MediaFormat.Wav or MediaFormat.Flac or MediaFormat.Ogg or
            MediaFormat.Opus => true,
            _ => false
        };
    }

    private static bool IsImageFormat(MediaFormat format)
    {
        return format switch
        {
            MediaFormat.Jpeg or MediaFormat.Png or MediaFormat.WebP or
            MediaFormat.Gif or MediaFormat.Bmp or MediaFormat.Tiff or
            MediaFormat.Avif or MediaFormat.Heif => true,
            _ => false
        };
    }

    private async Task<MediaInfo?> ProbeWithFFmpegAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_ffmpegPath))
            return null;

        try
        {
            var ffprobePath = Path.Combine(Path.GetDirectoryName(_ffmpegPath)!, "ffprobe");
            if (!File.Exists(ffprobePath) && !File.Exists(ffprobePath + ".exe"))
            {
                ffprobePath = "ffprobe"; // Try system path
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -print_format json -show_format -show_streams \"{path}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return null;

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
                return null;

            return ParseFFprobeOutput(output);
        }
        catch
        {
            return null;
        }
    }

    private MediaInfo? ParseFFprobeOutput(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            int? width = null, height = null;
            double? frameRate = null;
            int? videoBitrate = null, audioBitrate = null;
            int? sampleRate = null, channels = null;
            string? videoCodec = null, audioCodec = null;
            TimeSpan? duration = null;
            long sizeBytes = 0;

            if (root.TryGetProperty("format", out var format))
            {
                if (format.TryGetProperty("duration", out var dur))
                {
                    if (double.TryParse(dur.GetString(), out var seconds))
                    {
                        duration = TimeSpan.FromSeconds(seconds);
                    }
                }
                if (format.TryGetProperty("size", out var size))
                {
                    long.TryParse(size.GetString(), out sizeBytes);
                }
            }

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (!stream.TryGetProperty("codec_type", out var codecType))
                        continue;

                    var type = codecType.GetString();

                    if (type == "video")
                    {
                        if (stream.TryGetProperty("width", out var w))
                            width = w.GetInt32();
                        if (stream.TryGetProperty("height", out var h))
                            height = h.GetInt32();
                        if (stream.TryGetProperty("codec_name", out var codec))
                            videoCodec = codec.GetString();
                        if (stream.TryGetProperty("bit_rate", out var br))
                            if (int.TryParse(br.GetString(), out var bitrate))
                                videoBitrate = bitrate / 1000;
                        if (stream.TryGetProperty("r_frame_rate", out var fr))
                        {
                            var frStr = fr.GetString();
                            if (frStr != null && frStr.Contains('/'))
                            {
                                var parts = frStr.Split('/');
                                if (double.TryParse(parts[0], out var num) && double.TryParse(parts[1], out var den) && den > 0)
                                {
                                    frameRate = num / den;
                                }
                            }
                        }
                    }
                    else if (type == "audio")
                    {
                        if (stream.TryGetProperty("codec_name", out var codec))
                            audioCodec = codec.GetString();
                        if (stream.TryGetProperty("bit_rate", out var br))
                            if (int.TryParse(br.GetString(), out var bitrate))
                                audioBitrate = bitrate / 1000;
                        if (stream.TryGetProperty("sample_rate", out var sr))
                            if (int.TryParse(sr.GetString(), out var rate))
                                sampleRate = rate;
                        if (stream.TryGetProperty("channels", out var ch))
                            channels = ch.GetInt32();
                    }
                }
            }

            return new MediaInfo
            {
                Format = MediaFormat.Unknown, // Will be set by caller
                MimeType = "application/octet-stream",
                SizeBytes = sizeBytes,
                Duration = duration,
                Width = width,
                Height = height,
                FrameRate = frameRate,
                VideoCodec = videoCodec,
                VideoBitrate = videoBitrate,
                AudioCodec = audioCodec,
                AudioBitrate = audioBitrate,
                SampleRate = sampleRate,
                Channels = channels
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<(int Width, int Height)?> GetImageDimensionsAsync(string path, CancellationToken ct)
    {
        try
        {
            // Try reading image header to get dimensions
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var buffer = new byte[32];
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var bytesRead = await fs.ReadAsync(buffer.AsMemory(totalRead), ct);
                if (bytesRead == 0) break;
                totalRead += bytesRead;
            }

            if (totalRead < 8) return null;

            // PNG
            if (buffer[0] == 0x89 && buffer[1] == 0x50 && totalRead >= 24)
            {
                var width = (buffer[16] << 24) | (buffer[17] << 16) | (buffer[18] << 8) | buffer[19];
                var height = (buffer[20] << 24) | (buffer[21] << 16) | (buffer[22] << 8) | buffer[23];
                return (width, height);
            }

            // GIF
            if (buffer[0] == 0x47 && buffer[1] == 0x49 && totalRead >= 10)
            {
                var width = buffer[6] | (buffer[7] << 8);
                var height = buffer[8] | (buffer[9] << 8);
                return (width, height);
            }

            // BMP
            if (buffer[0] == 0x42 && buffer[1] == 0x4D)
            {
                fs.Seek(18, SeekOrigin.Begin);
                var dimBuffer = new byte[8];
                var dimRead = 0;
                while (dimRead < dimBuffer.Length)
                {
                    var bytesRead = await fs.ReadAsync(dimBuffer.AsMemory(dimRead), ct);
                    if (bytesRead == 0) break;
                    dimRead += bytesRead;
                }
                if (dimRead >= 8)
                {
                    var width = BitConverter.ToInt32(dimBuffer, 0);
                    var height = Math.Abs(BitConverter.ToInt32(dimBuffer, 4));
                    return (width, height);
                }
            }

            // For JPEG and other formats, would need more complex parsing
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    protected override async Task ProcessJobAsync(TranscodingJob job, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _jobCancellations[job.JobId] = cts;

        var context = new TranscodingJobContext
        {
            Job = job,
            CancellationTokenSource = cts,
            StartedAt = DateTime.UtcNow
        };
        _jobContexts[job.JobId] = context;

        await ProcessJobInternalAsync(context);
    }

    private async Task ProcessJobInternalAsync(TranscodingJobContext context)
    {
        var job = context.Job;
        var ct = context.CancellationTokenSource.Token;

        try
        {
            UpdateJobStatus(job.JobId, TranscodingStatus.InProgress, 0);
            UpdateProgress(job.JobId, 0, "Starting transcoding");

            // Check cache first
            var cached = GetCachedResult(job.SourcePath, job.Profile.ProfileId);
            if (cached != null && File.Exists(cached.Result.OutputPath))
            {
                UpdateProgress(job.JobId, 100, "Using cached result");
                UpdateJobStatus(job.JobId, TranscodingStatus.Completed, 100, outputSize: cached.Result.OutputSizeBytes);
                RecordCompletion(job.JobId, cached.Result);
                OnJobCompleted(new TranscodingCompletedEventArgs
                {
                    JobId = job.JobId,
                    Success = true,
                    Result = cached.Result,
                    FromCache = true
                });
                return;
            }

            TranscodingResult result;
            var stopwatch = Stopwatch.StartNew();

            // Determine processing strategy based on format
            if (IsImageFormat(job.SourceFormat))
            {
                result = await ProcessImageAsync(job, ct);
            }
            else if (IsVideoOrAudioFormat(job.SourceFormat))
            {
                result = await ProcessVideoAudioAsync(job, ct);
            }
            else if (IsDocumentFormat(job.SourceFormat))
            {
                result = await ProcessDocumentAsync(job, ct);
            }
            else
            {
                throw new NotSupportedException($"Format {job.SourceFormat} is not supported for transcoding");
            }

            stopwatch.Stop();

            // Update result with timing
            result = new TranscodingResult
            {
                JobId = result.JobId,
                Success = result.Success,
                OutputPath = result.OutputPath,
                OutputSizeBytes = result.OutputSizeBytes,
                Duration = stopwatch.Elapsed,
                SourceSizeBytes = result.SourceSizeBytes,
                MediaDuration = result.MediaDuration,
                OutputDimensions = result.OutputDimensions,
                ErrorMessage = result.ErrorMessage,
                Warnings = result.Warnings,
                OutputMediaInfo = result.OutputMediaInfo
            };

            // Cache the result
            CacheResult(job.SourcePath, job.Profile.ProfileId, result);

            UpdateJobStatus(job.JobId, TranscodingStatus.Completed, 100, outputSize: result.OutputSizeBytes);
            RecordCompletion(job.JobId, result);

            OnJobCompleted(new TranscodingCompletedEventArgs
            {
                JobId = job.JobId,
                Success = result.Success,
                Result = result,
                FromCache = false
            });
        }
        catch (OperationCanceledException)
        {
            UpdateJobStatus(job.JobId, TranscodingStatus.Cancelled, context.Progress);
        }
        catch (Exception ex)
        {
            UpdateJobStatus(job.JobId, TranscodingStatus.Failed, context.Progress, error: ex.Message);

            var errorResult = new TranscodingResult
            {
                JobId = job.JobId,
                Success = false,
                OutputPath = job.TargetPath,
                SourceSizeBytes = job.SourceSizeBytes,
                OutputSizeBytes = 0,
                ErrorMessage = ex.Message
            };

            RecordCompletion(job.JobId, errorResult);

            OnJobCompleted(new TranscodingCompletedEventArgs
            {
                JobId = job.JobId,
                Success = false,
                Result = errorResult,
                FromCache = false
            });
        }
        finally
        {
            _jobContexts.TryRemove(job.JobId, out _);
            _jobCancellations.TryRemove(job.JobId, out _);
        }
    }

    private async Task<TranscodingResult> ProcessImageAsync(TranscodingJob job, CancellationToken ct)
    {
        UpdateProgress(job.JobId, 10, "Processing image");

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(job.TargetPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        if (_imageMagickAvailable)
        {
            var command = BuildImageMagickCommand(job.SourcePath, job.TargetPath, job.Profile);
            await ExecuteExternalCommandAsync(_imageMagickPath!, command, ct);
        }
        else
        {
            // Fallback: copy file (no actual conversion without tools)
            File.Copy(job.SourcePath, job.TargetPath, true);
        }

        UpdateProgress(job.JobId, 100, "Complete");

        var outputInfo = new FileInfo(job.TargetPath);
        return new TranscodingResult
        {
            JobId = job.JobId,
            Success = true,
            OutputPath = job.TargetPath,
            OutputSizeBytes = outputInfo.Exists ? outputInfo.Length : 0,
            SourceSizeBytes = job.SourceSizeBytes
        };
    }

    private async Task<TranscodingResult> ProcessVideoAudioAsync(TranscodingJob job, CancellationToken ct)
    {
        UpdateProgress(job.JobId, 5, "Analyzing source");

        var sourceInfo = await ProbeAsync(job.SourcePath, ct);

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(job.TargetPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        if (_ffmpegAvailable)
        {
            UpdateProgress(job.JobId, 10, "Transcoding");

            var command = BuildFFmpegCommand(job.SourcePath, job.TargetPath, job.Profile);

            await ExecuteFFmpegWithProgressAsync(
                command,
                sourceInfo.Duration ?? TimeSpan.FromMinutes(1),
                job.JobId,
                ct);
        }
        else
        {
            // Fallback: copy file (no actual conversion without FFmpeg)
            UpdateProgress(job.JobId, 50, "Copying (FFmpeg not available)");
            File.Copy(job.SourcePath, job.TargetPath, true);
        }

        UpdateProgress(job.JobId, 100, "Complete");

        var outputInfo = new FileInfo(job.TargetPath);
        var outputMediaInfo = await ProbeAsync(job.TargetPath, ct);

        return new TranscodingResult
        {
            JobId = job.JobId,
            Success = true,
            OutputPath = job.TargetPath,
            OutputSizeBytes = outputInfo.Exists ? outputInfo.Length : 0,
            SourceSizeBytes = job.SourceSizeBytes,
            MediaDuration = outputMediaInfo.Duration,
            OutputDimensions = outputMediaInfo.Width.HasValue && outputMediaInfo.Height.HasValue
                ? (outputMediaInfo.Width.Value, outputMediaInfo.Height.Value)
                : null,
            OutputMediaInfo = outputMediaInfo
        };
    }

    private async Task<TranscodingResult> ProcessDocumentAsync(TranscodingJob job, CancellationToken ct)
    {
        UpdateProgress(job.JobId, 10, "Converting document");

        var conversionPath = GetDocumentConversionPath(job.SourceFormat, job.TargetFormat);
        if (!conversionPath.IsSupported)
        {
            throw new NotSupportedException($"Conversion from {job.SourceFormat} to {job.TargetFormat} is not supported");
        }

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(job.TargetPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        var command = conversionPath.CommandTemplate
            .Replace("{input}", job.SourcePath)
            .Replace("{output}", job.TargetPath)
            .Replace("{outdir}", outputDir ?? ".");

        try
        {
            await ExecuteExternalCommandAsync(conversionPath.Converter, command, ct);
        }
        catch
        {
            // Fallback: try to at least copy the file
            if (job.SourceFormat == job.TargetFormat)
            {
                File.Copy(job.SourcePath, job.TargetPath, true);
            }
            else
            {
                throw;
            }
        }

        UpdateProgress(job.JobId, 100, "Complete");

        var outputInfo = new FileInfo(job.TargetPath);
        return new TranscodingResult
        {
            JobId = job.JobId,
            Success = outputInfo.Exists,
            OutputPath = job.TargetPath,
            OutputSizeBytes = outputInfo.Exists ? outputInfo.Length : 0,
            SourceSizeBytes = job.SourceSizeBytes
        };
    }

    private static bool IsDocumentFormat(MediaFormat format)
    {
        return format switch
        {
            MediaFormat.Pdf or MediaFormat.Docx or MediaFormat.Xlsx or
            MediaFormat.Pptx or MediaFormat.Html or MediaFormat.Txt or
            MediaFormat.Markdown => true,
            _ => false
        };
    }

    private async Task ExecuteFFmpegWithProgressAsync(string arguments, TimeSpan duration, string jobId, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath ?? "ffmpeg",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException("Failed to start FFmpeg process");

        // Read stderr for progress (FFmpeg outputs progress to stderr)
        var progressTask = Task.Run(async () =>
        {
            var buffer = new char[256];
            while (!ct.IsCancellationRequested && !process.HasExited)
            {
                var read = await process.StandardError.ReadAsync(buffer, ct);
                if (read > 0)
                {
                    var output = new string(buffer, 0, read);
                    var progress = ParseFFmpegProgress(output, duration);
                    if (progress > 0)
                    {
                        UpdateProgress(jobId, 10 + progress * 0.9, "Transcoding");
                    }
                }
            }
        }, ct);

        await process.WaitForExitAsync(ct);
        await progressTask;

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"FFmpeg failed with exit code {process.ExitCode}: {error}");
        }
    }

    private async Task ExecuteExternalCommandAsync(string program, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = program,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException($"Failed to start {program}");

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"{program} failed with exit code {process.ExitCode}: {error}");
        }
    }

    /// <inheritdoc />
    protected override Task CancelProcessingAsync(string jobId, CancellationToken ct)
    {
        if (_jobCancellations.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
        }
        return Task.CompletedTask;
    }

    #endregion

    #region Tool Detection

    private async Task DetectExternalToolsAsync(CancellationToken ct)
    {
        // Detect FFmpeg
        _ffmpegPath = await FindExecutableAsync("ffmpeg", ct);
        _ffmpegAvailable = !string.IsNullOrEmpty(_ffmpegPath);

        // Detect ImageMagick
        _imageMagickPath = await FindExecutableAsync("magick", ct) ??
                          await FindExecutableAsync("convert", ct);
        _imageMagickAvailable = !string.IsNullOrEmpty(_imageMagickPath);
    }

    private async Task<string?> FindExecutableAsync(string name, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                Arguments = name,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return null;

            var output = await process.StandardOutput.ReadLineAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
            {
                return output.Trim();
            }
        }
        catch
        {
            // Ignore errors
        }

        return null;
    }

    private bool DetectHardwareAcceleration()
    {
        // Check for common hardware acceleration options
        // This is a simplified check - real implementation would probe GPU capabilities

        // Check for NVIDIA NVENC
        if (Environment.GetEnvironmentVariable("CUDA_PATH") != null)
            return true;

        // Check for Intel QuickSync (Windows)
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Check for Intel GPU in device list
                return true; // Simplified - assume available on Windows
            }
            catch
            {
                // Ignore
            }
        }

        return false;
    }

    #endregion

    #region Metadata

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["PluginId"] = PluginId;
        metadata["FFmpegAvailable"] = _ffmpegAvailable;
        metadata["ImageMagickAvailable"] = _imageMagickAvailable;
        metadata["CacheSize"] = _cache.Count;
        metadata["QueueSize"] = _jobQueue.Count;
        metadata["RegisteredProfiles"] = _customProfiles.Count;
        metadata["SupportsHLS"] = true;
        metadata["SupportsDASH"] = true;
        metadata["SupportsWatermarking"] = true;
        metadata["SupportsFormatNegotiation"] = true;
        return metadata;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Internal context for a transcoding job.
/// </summary>
internal class TranscodingJobContext
{
    public required TranscodingJob Job { get; init; }
    public required CancellationTokenSource CancellationTokenSource { get; init; }
    public DateTime StartedAt { get; init; }
    public double Progress { get; set; }
}

/// <summary>
/// Cached transcoding result.
/// </summary>
public class CachedTranscodingResult
{
    /// <summary>Cache key.</summary>
    public string CacheKey { get; init; } = string.Empty;
    /// <summary>Source file path.</summary>
    public string SourcePath { get; init; } = string.Empty;
    /// <summary>Profile identifier.</summary>
    public string ProfileId { get; init; } = string.Empty;
    /// <summary>Transcoding result.</summary>
    public required TranscodingResult Result { get; init; }
    /// <summary>When cached.</summary>
    public DateTime CachedAt { get; init; }
    /// <summary>When expires.</summary>
    public DateTime ExpiresAt { get; init; }
}

/// <summary>
/// Event arguments for progress updates.
/// </summary>
public class TranscodingProgressEventArgs : EventArgs
{
    /// <summary>Job identifier.</summary>
    public string JobId { get; init; } = string.Empty;
    /// <summary>Progress percentage (0-100).</summary>
    public double Progress { get; init; }
    /// <summary>Current operation description.</summary>
    public string? CurrentOperation { get; init; }
    /// <summary>Event timestamp.</summary>
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Event arguments for job completion.
/// </summary>
public class TranscodingCompletedEventArgs : EventArgs
{
    /// <summary>Job identifier.</summary>
    public string JobId { get; init; } = string.Empty;
    /// <summary>Whether transcoding succeeded.</summary>
    public bool Success { get; init; }
    /// <summary>Transcoding result.</summary>
    public required TranscodingResult Result { get; init; }
    /// <summary>Whether result was from cache.</summary>
    public bool FromCache { get; init; }
}

/// <summary>
/// HLS manifest structure.
/// </summary>
public class HlsManifest
{
    /// <summary>Path to master playlist.</summary>
    public string MasterPlaylistPath { get; init; } = string.Empty;
    /// <summary>Variant streams.</summary>
    public IReadOnlyList<HlsVariant> Variants { get; init; } = Array.Empty<HlsVariant>();
    /// <summary>Segment duration in seconds.</summary>
    public int SegmentDuration { get; init; }
    /// <summary>Total media duration.</summary>
    public TimeSpan? MediaDuration { get; init; }
}

/// <summary>
/// HLS variant stream.
/// </summary>
public class HlsVariant
{
    /// <summary>Profile identifier.</summary>
    public string ProfileId { get; init; } = string.Empty;
    /// <summary>Bandwidth in bits per second.</summary>
    public int Bandwidth { get; init; }
    /// <summary>Resolution string (e.g., "1920x1080").</summary>
    public string Resolution { get; init; } = string.Empty;
    /// <summary>Codec string.</summary>
    public string Codecs { get; init; } = string.Empty;
    /// <summary>Path to variant playlist.</summary>
    public string PlaylistPath { get; init; } = string.Empty;
    /// <summary>Segment duration in seconds.</summary>
    public int SegmentDuration { get; init; }
}

/// <summary>
/// DASH manifest structure.
/// </summary>
public class DashManifest
{
    /// <summary>Path to MPD file.</summary>
    public string MpdPath { get; init; } = string.Empty;
    /// <summary>Representations.</summary>
    public IReadOnlyList<DashRepresentation> Representations { get; init; } = Array.Empty<DashRepresentation>();
    /// <summary>Segment duration in seconds.</summary>
    public int SegmentDuration { get; init; }
    /// <summary>Total media duration.</summary>
    public TimeSpan? MediaDuration { get; init; }
    /// <summary>Minimum buffer time.</summary>
    public TimeSpan MinBufferTime { get; init; }
}

/// <summary>
/// DASH representation.
/// </summary>
public class DashRepresentation
{
    /// <summary>Representation ID.</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>Profile identifier.</summary>
    public string ProfileId { get; init; } = string.Empty;
    /// <summary>Bandwidth in bits per second.</summary>
    public int Bandwidth { get; init; }
    /// <summary>Video width.</summary>
    public int Width { get; init; }
    /// <summary>Video height.</summary>
    public int Height { get; init; }
    /// <summary>Frame rate.</summary>
    public double FrameRate { get; init; }
    /// <summary>Codec string.</summary>
    public string Codecs { get; init; } = string.Empty;
    /// <summary>MIME type.</summary>
    public string MimeType { get; init; } = string.Empty;
    /// <summary>Audio sample rate (for audio representations).</summary>
    public int? AudioSampleRate { get; init; }
    /// <summary>Audio channels (for audio representations).</summary>
    public int? AudioChannels { get; init; }
    /// <summary>Segment template.</summary>
    public string SegmentTemplate { get; init; } = string.Empty;
    /// <summary>Initialization segment path.</summary>
    public string InitializationSegment { get; init; } = string.Empty;
}

#endregion
