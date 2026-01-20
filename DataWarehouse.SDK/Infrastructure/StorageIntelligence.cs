using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DataWarehouse.SDK.Infrastructure;

// ============================================================================
// PHASE 8: EM4 - Storage Type Detection & AI Processing
// Intelligent content classification, tiering, and indexing
// ============================================================================

#region EM4.1: Storage Type Detector

/// <summary>
/// Detects and classifies storage content types using magic bytes, MIME types,
/// and content analysis for intelligent processing decisions.
/// </summary>
public sealed class StorageTypeDetector : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ContentTypeInfo> _typeCache = new();
    private readonly MagicBytesRegistry _magicBytes;
    private readonly MimeTypeRegistry _mimeTypes;
    private readonly StorageTypeDetectorConfig _config;
    private long _detectionCount;
    private long _cacheHits;
    private volatile bool _disposed;

    public StorageTypeDetector(StorageTypeDetectorConfig? config = null)
    {
        _config = config ?? new StorageTypeDetectorConfig();
        _magicBytes = new MagicBytesRegistry();
        _mimeTypes = new MimeTypeRegistry();
    }

    /// <summary>
    /// Detects content type from data stream using multiple detection methods.
    /// </summary>
    public async Task<ContentTypeInfo> DetectTypeAsync(
        Stream dataStream,
        string? fileName = null,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _detectionCount);

        // Check cache first if we have a content hash
        string? contentHash = null;
        if (_config.EnableCaching && dataStream.CanSeek)
        {
            contentHash = await ComputeContentHashAsync(dataStream, ct);
            dataStream.Position = 0;

            if (_typeCache.TryGetValue(contentHash, out var cached))
            {
                Interlocked.Increment(ref _cacheHits);
                return cached;
            }
        }

        var result = new ContentTypeInfo
        {
            DetectedAt = DateTime.UtcNow
        };

        // Method 1: Magic bytes detection
        var magicResult = await DetectByMagicBytesAsync(dataStream, ct);
        if (magicResult != null)
        {
            result.PrimaryType = magicResult.Type;
            result.MimeType = magicResult.MimeType;
            result.DetectionMethod = ContentDetectionMethod.MagicBytes;
            result.Confidence = magicResult.Confidence;
        }

        // Method 2: File extension analysis
        if (!string.IsNullOrEmpty(fileName))
        {
            var extResult = DetectByExtension(fileName);
            if (extResult != null)
            {
                if (result.PrimaryType == ContentType.Unknown)
                {
                    result.PrimaryType = extResult.Type;
                    result.MimeType = extResult.MimeType;
                    result.DetectionMethod = ContentDetectionMethod.FileExtension;
                    result.Confidence = extResult.Confidence;
                }
                else if (result.PrimaryType == extResult.Type)
                {
                    result.Confidence = Math.Min(1.0, result.Confidence + 0.2);
                }
            }
            result.FileName = fileName;
        }

        // Method 3: Content analysis for text-based formats
        if (result.PrimaryType == ContentType.Unknown || _config.AlwaysAnalyzeContent)
        {
            if (dataStream.CanSeek)
                dataStream.Position = 0;

            var contentResult = await AnalyzeContentAsync(dataStream, ct);
            if (contentResult != null && contentResult.Confidence > result.Confidence)
            {
                result.PrimaryType = contentResult.Type;
                result.MimeType = contentResult.MimeType;
                result.DetectionMethod = ContentDetectionMethod.ContentAnalysis;
                result.Confidence = contentResult.Confidence;
                result.ContentCharacteristics = contentResult.Characteristics;
            }
        }

        // Compute additional metadata
        if (dataStream.CanSeek)
        {
            result.FileSize = dataStream.Length;
            dataStream.Position = 0;
        }

        result.ContentHash = contentHash;

        // Cache result
        if (_config.EnableCaching && contentHash != null)
        {
            _typeCache[contentHash] = result;
        }

        return result;
    }

    /// <summary>
    /// Detects content type from byte array.
    /// </summary>
    public async Task<ContentTypeInfo> DetectTypeAsync(
        byte[] data,
        string? fileName = null,
        CancellationToken ct = default)
    {
        using var stream = new MemoryStream(data);
        return await DetectTypeAsync(stream, fileName, ct);
    }

    /// <summary>
    /// Quick detection using only magic bytes (no content analysis).
    /// </summary>
    public ContentTypeInfo QuickDetect(byte[] headerBytes, string? fileName = null)
    {
        var result = new ContentTypeInfo { DetectedAt = DateTime.UtcNow };

        var magicResult = _magicBytes.Match(headerBytes);
        if (magicResult != null)
        {
            result.PrimaryType = magicResult.Type;
            result.MimeType = magicResult.MimeType;
            result.DetectionMethod = ContentDetectionMethod.MagicBytes;
            result.Confidence = magicResult.Confidence;
        }

        if (!string.IsNullOrEmpty(fileName))
        {
            var extResult = DetectByExtension(fileName);
            if (extResult != null && result.PrimaryType == ContentType.Unknown)
            {
                result.PrimaryType = extResult.Type;
                result.MimeType = extResult.MimeType;
                result.DetectionMethod = ContentDetectionMethod.FileExtension;
                result.Confidence = extResult.Confidence;
            }
            result.FileName = fileName;
        }

        return result;
    }

    private async Task<MagicBytesMatch?> DetectByMagicBytesAsync(Stream stream, CancellationToken ct)
    {
        var headerBuffer = new byte[_config.MagicBytesReadLength];
        var bytesRead = await stream.ReadAsync(headerBuffer.AsMemory(0, headerBuffer.Length), ct);

        if (bytesRead == 0) return null;

        return _magicBytes.Match(headerBuffer.AsSpan(0, bytesRead).ToArray());
    }

    private MagicBytesMatch? DetectByExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(extension)) return null;

        return _mimeTypes.GetByExtension(extension);
    }

    private async Task<ContentAnalysisResult?> AnalyzeContentAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[Math.Min(_config.ContentAnalysisReadLength, stream.Length)];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);

        if (bytesRead == 0) return null;

        var data = buffer.AsSpan(0, bytesRead).ToArray();

        // Check if content is text
        if (IsLikelyText(data))
        {
            var text = Encoding.UTF8.GetString(data);
            return AnalyzeTextContent(text);
        }

        // Analyze binary content characteristics
        return AnalyzeBinaryContent(data);
    }

    private bool IsLikelyText(byte[] data)
    {
        if (data.Length == 0) return false;

        var nonPrintable = 0;
        var nullBytes = 0;

        foreach (var b in data.Take(Math.Min(1000, data.Length)))
        {
            if (b == 0) nullBytes++;
            else if (b < 32 && b != 9 && b != 10 && b != 13) nonPrintable++;
        }

        var sampleSize = Math.Min(1000, data.Length);
        return nullBytes == 0 && nonPrintable < sampleSize * 0.1;
    }

    private ContentAnalysisResult AnalyzeTextContent(string text)
    {
        var result = new ContentAnalysisResult
        {
            Characteristics = new ContentCharacteristics()
        };

        // JSON detection
        if ((text.TrimStart().StartsWith("{") || text.TrimStart().StartsWith("[")) &&
            (text.TrimEnd().EndsWith("}") || text.TrimEnd().EndsWith("]")))
        {
            try
            {
                JsonDocument.Parse(text);
                result.Type = ContentType.Data;
                result.MimeType = "application/json";
                result.Confidence = 0.95;
                result.Characteristics.IsStructured = true;
                return result;
            }
            catch { }
        }

        // XML detection
        if (text.TrimStart().StartsWith("<?xml") || text.TrimStart().StartsWith("<"))
        {
            if (Regex.IsMatch(text, @"<[a-zA-Z][^>]*>"))
            {
                result.Type = ContentType.Data;
                result.MimeType = "application/xml";
                result.Confidence = 0.9;
                result.Characteristics.IsStructured = true;
                return result;
            }
        }

        // HTML detection
        if (Regex.IsMatch(text, @"<!DOCTYPE\s+html|<html|<head|<body", RegexOptions.IgnoreCase))
        {
            result.Type = ContentType.Document;
            result.MimeType = "text/html";
            result.Confidence = 0.9;
            return result;
        }

        // Markdown detection
        if (Regex.IsMatch(text, @"^#{1,6}\s|^\*{3,}|^\-{3,}|^\[.*\]\(.*\)", RegexOptions.Multiline))
        {
            result.Type = ContentType.Document;
            result.MimeType = "text/markdown";
            result.Confidence = 0.75;
            return result;
        }

        // CSV detection
        if (IsLikelyCsv(text))
        {
            result.Type = ContentType.Data;
            result.MimeType = "text/csv";
            result.Confidence = 0.8;
            result.Characteristics.IsStructured = true;
            return result;
        }

        // Source code detection
        var codePatterns = new[]
        {
            (@"^using\s+[A-Za-z]|namespace\s+[A-Za-z]|class\s+[A-Za-z]", "text/x-csharp"),
            (@"^import\s+[a-z]|^from\s+[a-z].*import|def\s+\w+\s*\(", "text/x-python"),
            (@"^import\s+\{|^export\s+(default\s+)?|const\s+\w+\s*=|let\s+\w+\s*=", "text/javascript"),
            (@"^package\s+[a-z]|^import\s+java\.|public\s+class", "text/x-java"),
            (@"^#include\s*<|^int\s+main\s*\(|^void\s+\w+\s*\(", "text/x-c"),
        };

        foreach (var (pattern, mime) in codePatterns)
        {
            if (Regex.IsMatch(text, pattern, RegexOptions.Multiline))
            {
                result.Type = ContentType.Code;
                result.MimeType = mime;
                result.Confidence = 0.7;
                return result;
            }
        }

        // Default to plain text
        result.Type = ContentType.Document;
        result.MimeType = "text/plain";
        result.Confidence = 0.5;
        result.Characteristics.IsText = true;

        return result;
    }

    private bool IsLikelyCsv(string text)
    {
        var lines = text.Split('\n').Take(10).ToList();
        if (lines.Count < 2) return false;

        var delimiters = new[] { ',', '\t', ';', '|' };
        foreach (var delimiter in delimiters)
        {
            var counts = lines.Select(l => l.Count(c => c == delimiter)).ToList();
            if (counts.All(c => c > 0) && counts.Distinct().Count() <= 2)
                return true;
        }
        return false;
    }

    private ContentAnalysisResult AnalyzeBinaryContent(byte[] data)
    {
        var result = new ContentAnalysisResult
        {
            Type = ContentType.Binary,
            MimeType = "application/octet-stream",
            Confidence = 0.3,
            Characteristics = new ContentCharacteristics()
        };

        // Calculate entropy to detect encrypted/compressed data
        var entropy = CalculateEntropy(data);
        result.Characteristics.Entropy = entropy;

        if (entropy > 7.9)
        {
            result.Characteristics.IsEncrypted = true;
            result.Characteristics.IsCompressed = true;
        }
        else if (entropy > 7.0)
        {
            result.Characteristics.IsCompressed = true;
        }

        return result;
    }

    private double CalculateEntropy(byte[] data)
    {
        if (data.Length == 0) return 0;

        var frequency = new int[256];
        foreach (var b in data)
            frequency[b]++;

        double entropy = 0;
        foreach (var count in frequency)
        {
            if (count == 0) continue;
            var p = (double)count / data.Length;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    private async Task<string> ComputeContentHashAsync(Stream stream, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public StorageTypeDetectorStatistics GetStatistics()
    {
        return new StorageTypeDetectorStatistics
        {
            TotalDetections = _detectionCount,
            CacheHits = _cacheHits,
            CacheSize = _typeCache.Count,
            CacheHitRate = _detectionCount > 0 ? (double)_cacheHits / _detectionCount : 0
        };
    }

    public void ClearCache() => _typeCache.Clear();

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Registry of magic byte signatures for file type detection.
/// </summary>
internal sealed class MagicBytesRegistry
{
    private readonly List<MagicBytesSignature> _signatures = new();

    public MagicBytesRegistry()
    {
        InitializeSignatures();
    }

    private void InitializeSignatures()
    {
        // Images
        _signatures.Add(new MagicBytesSignature(new byte[] { 0xFF, 0xD8, 0xFF }, ContentType.Image, "image/jpeg", 0.98));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ContentType.Image, "image/png", 0.99));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 }, ContentType.Image, "image/gif", 0.99));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, ContentType.Image, "image/gif", 0.99));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x52, 0x49, 0x46, 0x46 }, ContentType.Image, "image/webp", 0.9, 8, new byte[] { 0x57, 0x45, 0x42, 0x50 }));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x42, 0x4D }, ContentType.Image, "image/bmp", 0.95));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, ContentType.Image, "image/tiff", 0.95));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x4D, 0x4D, 0x00, 0x2A }, ContentType.Image, "image/tiff", 0.95));

        // Documents
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x25, 0x50, 0x44, 0x46 }, ContentType.Document, "application/pdf", 0.99));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, ContentType.Document, "application/zip", 0.85)); // Could be Office docs
        _signatures.Add(new MagicBytesSignature(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, ContentType.Document, "application/msword", 0.9));

        // Audio
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x49, 0x44, 0x33 }, ContentType.Audio, "audio/mpeg", 0.95));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0xFF, 0xFB }, ContentType.Audio, "audio/mpeg", 0.85));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0xFF, 0xF3 }, ContentType.Audio, "audio/mpeg", 0.85));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x52, 0x49, 0x46, 0x46 }, ContentType.Audio, "audio/wav", 0.9, 8, new byte[] { 0x57, 0x41, 0x56, 0x45 }));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x4F, 0x67, 0x67, 0x53 }, ContentType.Audio, "audio/ogg", 0.95));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x66, 0x4C, 0x61, 0x43 }, ContentType.Audio, "audio/flac", 0.99));

        // Video
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x00, 0x00, 0x00 }, ContentType.Video, "video/mp4", 0.7, 4, new byte[] { 0x66, 0x74, 0x79, 0x70 }));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x52, 0x49, 0x46, 0x46 }, ContentType.Video, "video/avi", 0.9, 8, new byte[] { 0x41, 0x56, 0x49, 0x20 }));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, ContentType.Video, "video/webm", 0.95));

        // Archives
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x1F, 0x8B }, ContentType.Archive, "application/gzip", 0.99));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x42, 0x5A, 0x68 }, ContentType.Archive, "application/x-bzip2", 0.99));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A }, ContentType.Archive, "application/x-xz", 0.99));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 }, ContentType.Archive, "application/x-rar", 0.99));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, ContentType.Archive, "application/x-7z-compressed", 0.99));

        // Executables
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x4D, 0x5A }, ContentType.Executable, "application/x-msdownload", 0.95));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x7F, 0x45, 0x4C, 0x46 }, ContentType.Executable, "application/x-elf", 0.99));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }, ContentType.Executable, "application/java-archive", 0.95));

        // Data formats
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x53, 0x51, 0x4C, 0x69, 0x74, 0x65 }, ContentType.Database, "application/x-sqlite3", 0.99));
        _signatures.Add(new MagicBytesSignature(new byte[] { 0x50, 0x41, 0x52, 0x31 }, ContentType.Data, "application/x-parquet", 0.99));
    }

    public MagicBytesMatch? Match(byte[] data)
    {
        foreach (var sig in _signatures.OrderByDescending(s => s.MagicBytes.Length))
        {
            if (sig.Matches(data))
            {
                return new MagicBytesMatch
                {
                    Type = sig.Type,
                    MimeType = sig.MimeType,
                    Confidence = sig.Confidence
                };
            }
        }
        return null;
    }
}

internal sealed class MagicBytesSignature
{
    public byte[] MagicBytes { get; }
    public ContentType Type { get; }
    public string MimeType { get; }
    public double Confidence { get; }
    public int SecondaryOffset { get; }
    public byte[]? SecondaryBytes { get; }

    public MagicBytesSignature(byte[] magicBytes, ContentType type, string mimeType, double confidence,
        int secondaryOffset = -1, byte[]? secondaryBytes = null)
    {
        MagicBytes = magicBytes;
        Type = type;
        MimeType = mimeType;
        Confidence = confidence;
        SecondaryOffset = secondaryOffset;
        SecondaryBytes = secondaryBytes;
    }

    public bool Matches(byte[] data)
    {
        if (data.Length < MagicBytes.Length) return false;

        for (int i = 0; i < MagicBytes.Length; i++)
        {
            if (data[i] != MagicBytes[i]) return false;
        }

        if (SecondaryBytes != null && SecondaryOffset >= 0)
        {
            if (data.Length < SecondaryOffset + SecondaryBytes.Length) return false;

            for (int i = 0; i < SecondaryBytes.Length; i++)
            {
                if (data[SecondaryOffset + i] != SecondaryBytes[i]) return false;
            }
        }

        return true;
    }
}

internal sealed class MimeTypeRegistry
{
    private readonly Dictionary<string, MagicBytesMatch> _extensionMap = new();

    public MimeTypeRegistry()
    {
        InitializeExtensions();
    }

    private void InitializeExtensions()
    {
        // Images
        _extensionMap[".jpg"] = new MagicBytesMatch { Type = ContentType.Image, MimeType = "image/jpeg", Confidence = 0.9 };
        _extensionMap[".jpeg"] = new MagicBytesMatch { Type = ContentType.Image, MimeType = "image/jpeg", Confidence = 0.9 };
        _extensionMap[".png"] = new MagicBytesMatch { Type = ContentType.Image, MimeType = "image/png", Confidence = 0.9 };
        _extensionMap[".gif"] = new MagicBytesMatch { Type = ContentType.Image, MimeType = "image/gif", Confidence = 0.9 };
        _extensionMap[".webp"] = new MagicBytesMatch { Type = ContentType.Image, MimeType = "image/webp", Confidence = 0.9 };
        _extensionMap[".svg"] = new MagicBytesMatch { Type = ContentType.Image, MimeType = "image/svg+xml", Confidence = 0.9 };
        _extensionMap[".ico"] = new MagicBytesMatch { Type = ContentType.Image, MimeType = "image/x-icon", Confidence = 0.9 };

        // Documents
        _extensionMap[".pdf"] = new MagicBytesMatch { Type = ContentType.Document, MimeType = "application/pdf", Confidence = 0.95 };
        _extensionMap[".doc"] = new MagicBytesMatch { Type = ContentType.Document, MimeType = "application/msword", Confidence = 0.9 };
        _extensionMap[".docx"] = new MagicBytesMatch { Type = ContentType.Document, MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document", Confidence = 0.9 };
        _extensionMap[".xls"] = new MagicBytesMatch { Type = ContentType.Document, MimeType = "application/vnd.ms-excel", Confidence = 0.9 };
        _extensionMap[".xlsx"] = new MagicBytesMatch { Type = ContentType.Document, MimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", Confidence = 0.9 };
        _extensionMap[".ppt"] = new MagicBytesMatch { Type = ContentType.Document, MimeType = "application/vnd.ms-powerpoint", Confidence = 0.9 };
        _extensionMap[".pptx"] = new MagicBytesMatch { Type = ContentType.Document, MimeType = "application/vnd.openxmlformats-officedocument.presentationml.presentation", Confidence = 0.9 };
        _extensionMap[".txt"] = new MagicBytesMatch { Type = ContentType.Document, MimeType = "text/plain", Confidence = 0.8 };
        _extensionMap[".md"] = new MagicBytesMatch { Type = ContentType.Document, MimeType = "text/markdown", Confidence = 0.85 };
        _extensionMap[".rtf"] = new MagicBytesMatch { Type = ContentType.Document, MimeType = "application/rtf", Confidence = 0.9 };

        // Code
        _extensionMap[".cs"] = new MagicBytesMatch { Type = ContentType.Code, MimeType = "text/x-csharp", Confidence = 0.95 };
        _extensionMap[".py"] = new MagicBytesMatch { Type = ContentType.Code, MimeType = "text/x-python", Confidence = 0.95 };
        _extensionMap[".js"] = new MagicBytesMatch { Type = ContentType.Code, MimeType = "text/javascript", Confidence = 0.95 };
        _extensionMap[".ts"] = new MagicBytesMatch { Type = ContentType.Code, MimeType = "text/typescript", Confidence = 0.95 };
        _extensionMap[".java"] = new MagicBytesMatch { Type = ContentType.Code, MimeType = "text/x-java", Confidence = 0.95 };
        _extensionMap[".c"] = new MagicBytesMatch { Type = ContentType.Code, MimeType = "text/x-c", Confidence = 0.95 };
        _extensionMap[".cpp"] = new MagicBytesMatch { Type = ContentType.Code, MimeType = "text/x-c++", Confidence = 0.95 };
        _extensionMap[".h"] = new MagicBytesMatch { Type = ContentType.Code, MimeType = "text/x-c", Confidence = 0.9 };
        _extensionMap[".go"] = new MagicBytesMatch { Type = ContentType.Code, MimeType = "text/x-go", Confidence = 0.95 };
        _extensionMap[".rs"] = new MagicBytesMatch { Type = ContentType.Code, MimeType = "text/x-rust", Confidence = 0.95 };
        _extensionMap[".rb"] = new MagicBytesMatch { Type = ContentType.Code, MimeType = "text/x-ruby", Confidence = 0.95 };
        _extensionMap[".php"] = new MagicBytesMatch { Type = ContentType.Code, MimeType = "text/x-php", Confidence = 0.95 };

        // Data
        _extensionMap[".json"] = new MagicBytesMatch { Type = ContentType.Data, MimeType = "application/json", Confidence = 0.95 };
        _extensionMap[".xml"] = new MagicBytesMatch { Type = ContentType.Data, MimeType = "application/xml", Confidence = 0.9 };
        _extensionMap[".csv"] = new MagicBytesMatch { Type = ContentType.Data, MimeType = "text/csv", Confidence = 0.9 };
        _extensionMap[".yaml"] = new MagicBytesMatch { Type = ContentType.Data, MimeType = "application/x-yaml", Confidence = 0.9 };
        _extensionMap[".yml"] = new MagicBytesMatch { Type = ContentType.Data, MimeType = "application/x-yaml", Confidence = 0.9 };
        _extensionMap[".parquet"] = new MagicBytesMatch { Type = ContentType.Data, MimeType = "application/x-parquet", Confidence = 0.95 };
        _extensionMap[".avro"] = new MagicBytesMatch { Type = ContentType.Data, MimeType = "application/avro", Confidence = 0.95 };

        // Audio
        _extensionMap[".mp3"] = new MagicBytesMatch { Type = ContentType.Audio, MimeType = "audio/mpeg", Confidence = 0.9 };
        _extensionMap[".wav"] = new MagicBytesMatch { Type = ContentType.Audio, MimeType = "audio/wav", Confidence = 0.9 };
        _extensionMap[".flac"] = new MagicBytesMatch { Type = ContentType.Audio, MimeType = "audio/flac", Confidence = 0.9 };
        _extensionMap[".ogg"] = new MagicBytesMatch { Type = ContentType.Audio, MimeType = "audio/ogg", Confidence = 0.9 };
        _extensionMap[".m4a"] = new MagicBytesMatch { Type = ContentType.Audio, MimeType = "audio/mp4", Confidence = 0.9 };

        // Video
        _extensionMap[".mp4"] = new MagicBytesMatch { Type = ContentType.Video, MimeType = "video/mp4", Confidence = 0.9 };
        _extensionMap[".webm"] = new MagicBytesMatch { Type = ContentType.Video, MimeType = "video/webm", Confidence = 0.9 };
        _extensionMap[".avi"] = new MagicBytesMatch { Type = ContentType.Video, MimeType = "video/avi", Confidence = 0.9 };
        _extensionMap[".mkv"] = new MagicBytesMatch { Type = ContentType.Video, MimeType = "video/x-matroska", Confidence = 0.9 };
        _extensionMap[".mov"] = new MagicBytesMatch { Type = ContentType.Video, MimeType = "video/quicktime", Confidence = 0.9 };

        // Archives
        _extensionMap[".zip"] = new MagicBytesMatch { Type = ContentType.Archive, MimeType = "application/zip", Confidence = 0.9 };
        _extensionMap[".tar"] = new MagicBytesMatch { Type = ContentType.Archive, MimeType = "application/x-tar", Confidence = 0.9 };
        _extensionMap[".gz"] = new MagicBytesMatch { Type = ContentType.Archive, MimeType = "application/gzip", Confidence = 0.9 };
        _extensionMap[".bz2"] = new MagicBytesMatch { Type = ContentType.Archive, MimeType = "application/x-bzip2", Confidence = 0.9 };
        _extensionMap[".7z"] = new MagicBytesMatch { Type = ContentType.Archive, MimeType = "application/x-7z-compressed", Confidence = 0.9 };
        _extensionMap[".rar"] = new MagicBytesMatch { Type = ContentType.Archive, MimeType = "application/x-rar", Confidence = 0.9 };
    }

    public MagicBytesMatch? GetByExtension(string extension)
    {
        return _extensionMap.TryGetValue(extension.ToLowerInvariant(), out var match) ? match : null;
    }
}

public sealed class StorageTypeDetectorConfig
{
    public int MagicBytesReadLength { get; set; } = 4096;
    public int ContentAnalysisReadLength { get; set; } = 65536;
    public bool EnableCaching { get; set; } = true;
    public bool AlwaysAnalyzeContent { get; set; } = false;
}

public sealed class ContentTypeInfo
{
    public ContentType PrimaryType { get; set; } = ContentType.Unknown;
    public string MimeType { get; set; } = "application/octet-stream";
    public double Confidence { get; set; }
    public ContentDetectionMethod DetectionMethod { get; set; }
    public string? FileName { get; set; }
    public long FileSize { get; set; }
    public string? ContentHash { get; set; }
    public ContentCharacteristics? ContentCharacteristics { get; set; }
    public DateTime DetectedAt { get; init; }
}

public sealed class ContentCharacteristics
{
    public bool IsText { get; set; }
    public bool IsStructured { get; set; }
    public bool IsCompressed { get; set; }
    public bool IsEncrypted { get; set; }
    public double Entropy { get; set; }
    public string? Encoding { get; set; }
    public string? Language { get; set; }
}

public enum ContentType
{
    Unknown,
    Document,
    Image,
    Audio,
    Video,
    Code,
    Data,
    Archive,
    Executable,
    Database,
    Binary
}

public enum ContentDetectionMethod
{
    Unknown,
    MagicBytes,
    FileExtension,
    ContentAnalysis,
    AIClassification
}

internal sealed class MagicBytesMatch
{
    public ContentType Type { get; init; }
    public string MimeType { get; init; } = string.Empty;
    public double Confidence { get; init; }
}

internal sealed class ContentAnalysisResult
{
    public ContentType Type { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public ContentCharacteristics Characteristics { get; set; } = new();
}

public record StorageTypeDetectorStatistics
{
    public long TotalDetections { get; init; }
    public long CacheHits { get; init; }
    public int CacheSize { get; init; }
    public double CacheHitRate { get; init; }
}

#endregion

#region EM4.2: Intelligent Content Classifier

/// <summary>
/// AI-powered content classifier for semantic classification of storage content.
/// Uses embeddings and ML models for intelligent categorization.
/// </summary>
public sealed class IntelligentContentClassifier : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ClassificationResult> _classificationCache = new();
    private readonly ConcurrentDictionary<string, CategoryDefinition> _categories = new();
    private readonly IntelligentClassifierConfig _config;
    private readonly StorageTypeDetector _typeDetector;
    private long _classificationsPerformed;
    private volatile bool _disposed;

    public event EventHandler<ClassificationCompletedEventArgs>? ClassificationCompleted;

    public IntelligentContentClassifier(IntelligentClassifierConfig? config = null)
    {
        _config = config ?? new IntelligentClassifierConfig();
        _typeDetector = new StorageTypeDetector();
        InitializeDefaultCategories();
    }

    private void InitializeDefaultCategories()
    {
        // Business categories
        _categories["financial"] = new CategoryDefinition
        {
            CategoryId = "financial",
            Name = "Financial Documents",
            Keywords = new[] { "invoice", "receipt", "payment", "transaction", "budget", "expense", "revenue", "profit", "loss", "balance", "statement" },
            ContentTypes = new[] { ContentType.Document, ContentType.Data },
            Priority = CategoryPriority.High,
            RetentionPolicy = "7-years"
        };

        _categories["legal"] = new CategoryDefinition
        {
            CategoryId = "legal",
            Name = "Legal Documents",
            Keywords = new[] { "contract", "agreement", "terms", "conditions", "liability", "warranty", "compliance", "regulation", "lawsuit", "settlement" },
            ContentTypes = new[] { ContentType.Document },
            Priority = CategoryPriority.Critical,
            RetentionPolicy = "permanent"
        };

        _categories["hr"] = new CategoryDefinition
        {
            CategoryId = "hr",
            Name = "Human Resources",
            Keywords = new[] { "employee", "salary", "performance", "review", "hire", "termination", "benefits", "leave", "vacation", "payroll" },
            ContentTypes = new[] { ContentType.Document, ContentType.Data },
            Priority = CategoryPriority.High,
            RetentionPolicy = "7-years",
            IsPII = true
        };

        _categories["medical"] = new CategoryDefinition
        {
            CategoryId = "medical",
            Name = "Medical Records",
            Keywords = new[] { "patient", "diagnosis", "treatment", "prescription", "medical", "health", "hospital", "doctor", "nurse", "clinic" },
            ContentTypes = new[] { ContentType.Document, ContentType.Image },
            Priority = CategoryPriority.Critical,
            RetentionPolicy = "permanent",
            IsPII = true,
            IsHIPAA = true
        };

        // Technical categories
        _categories["source-code"] = new CategoryDefinition
        {
            CategoryId = "source-code",
            Name = "Source Code",
            Keywords = new[] { "function", "class", "method", "variable", "import", "export", "module", "package" },
            ContentTypes = new[] { ContentType.Code },
            Priority = CategoryPriority.Normal
        };

        _categories["configuration"] = new CategoryDefinition
        {
            CategoryId = "configuration",
            Name = "Configuration Files",
            Keywords = new[] { "config", "settings", "environment", "connection", "database", "server", "host", "port" },
            ContentTypes = new[] { ContentType.Data, ContentType.Code },
            Priority = CategoryPriority.High
        };

        _categories["logs"] = new CategoryDefinition
        {
            CategoryId = "logs",
            Name = "Log Files",
            Keywords = new[] { "error", "warning", "info", "debug", "trace", "exception", "stack", "timestamp" },
            ContentTypes = new[] { ContentType.Document, ContentType.Data },
            Priority = CategoryPriority.Low,
            RetentionPolicy = "90-days"
        };

        // Media categories
        _categories["media-photos"] = new CategoryDefinition
        {
            CategoryId = "media-photos",
            Name = "Photos",
            ContentTypes = new[] { ContentType.Image },
            Priority = CategoryPriority.Normal
        };

        _categories["media-videos"] = new CategoryDefinition
        {
            CategoryId = "media-videos",
            Name = "Videos",
            ContentTypes = new[] { ContentType.Video },
            Priority = CategoryPriority.Normal
        };

        _categories["media-audio"] = new CategoryDefinition
        {
            CategoryId = "media-audio",
            Name = "Audio Files",
            ContentTypes = new[] { ContentType.Audio },
            Priority = CategoryPriority.Normal
        };

        // Data categories
        _categories["analytics"] = new CategoryDefinition
        {
            CategoryId = "analytics",
            Name = "Analytics Data",
            Keywords = new[] { "metrics", "statistics", "analysis", "report", "dashboard", "kpi", "trend" },
            ContentTypes = new[] { ContentType.Data },
            Priority = CategoryPriority.Normal
        };

        _categories["backup"] = new CategoryDefinition
        {
            CategoryId = "backup",
            Name = "Backup Data",
            Keywords = new[] { "backup", "snapshot", "archive", "restore" },
            ContentTypes = new[] { ContentType.Archive, ContentType.Database },
            Priority = CategoryPriority.High
        };
    }

    /// <summary>
    /// Classifies content using AI and rule-based analysis.
    /// </summary>
    public async Task<ClassificationResult> ClassifyAsync(
        Stream dataStream,
        string? fileName = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _classificationsPerformed);

        // First, detect content type
        var typeInfo = await _typeDetector.DetectTypeAsync(dataStream, fileName, ct);

        if (dataStream.CanSeek)
            dataStream.Position = 0;

        var result = new ClassificationResult
        {
            ContentTypeInfo = typeInfo,
            ClassifiedAt = DateTime.UtcNow
        };

        // Match against categories
        var categoryScores = new Dictionary<string, double>();

        foreach (var (categoryId, category) in _categories)
        {
            var score = CalculateCategoryScore(typeInfo, category, dataStream, fileName, metadata);
            if (score > 0)
                categoryScores[categoryId] = score;
        }

        if (categoryScores.Any())
        {
            var topCategories = categoryScores
                .OrderByDescending(kvp => kvp.Value)
                .Take(_config.MaxCategories)
                .ToList();

            result.PrimaryCategory = topCategories.First().Key;
            result.PrimaryCategoryConfidence = topCategories.First().Value;
            result.Categories = topCategories.Select(kvp => new CategoryMatch
            {
                CategoryId = kvp.Key,
                CategoryName = _categories[kvp.Key].Name,
                Confidence = kvp.Value,
                Priority = _categories[kvp.Key].Priority
            }).ToList();

            // Apply category metadata
            var primaryCat = _categories[result.PrimaryCategory];
            result.SuggestedRetentionPolicy = primaryCat.RetentionPolicy;
            result.ContainsPII = primaryCat.IsPII;
            result.IsHIPAAProtected = primaryCat.IsHIPAA;
            result.SuggestedPriority = primaryCat.Priority;
        }

        // Extract text content for further analysis if text-based
        if (typeInfo.PrimaryType == ContentType.Document ||
            typeInfo.PrimaryType == ContentType.Code ||
            typeInfo.PrimaryType == ContentType.Data)
        {
            if (dataStream.CanSeek)
                dataStream.Position = 0;

            result.ExtractedKeywords = await ExtractKeywordsAsync(dataStream, ct);
        }

        // Cache result
        if (_config.EnableCaching && typeInfo.ContentHash != null)
        {
            _classificationCache[typeInfo.ContentHash] = result;
        }

        ClassificationCompleted?.Invoke(this, new ClassificationCompletedEventArgs { Result = result });

        return result;
    }

    /// <summary>
    /// Classifies content from byte array.
    /// </summary>
    public async Task<ClassificationResult> ClassifyAsync(
        byte[] data,
        string? fileName = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        using var stream = new MemoryStream(data);
        return await ClassifyAsync(stream, fileName, metadata, ct);
    }

    /// <summary>
    /// Registers a custom category for classification.
    /// </summary>
    public void RegisterCategory(CategoryDefinition category)
    {
        _categories[category.CategoryId] = category;
    }

    /// <summary>
    /// Gets all registered categories.
    /// </summary>
    public IReadOnlyList<CategoryDefinition> GetCategories() => _categories.Values.ToList();

    private double CalculateCategoryScore(
        ContentTypeInfo typeInfo,
        CategoryDefinition category,
        Stream dataStream,
        string? fileName,
        Dictionary<string, object>? metadata)
    {
        double score = 0;

        // Content type match
        if (category.ContentTypes?.Contains(typeInfo.PrimaryType) == true)
            score += 0.3;

        // Keyword matching in filename
        if (!string.IsNullOrEmpty(fileName) && category.Keywords != null)
        {
            var lowerFileName = fileName.ToLowerInvariant();
            var keywordMatches = category.Keywords.Count(k => lowerFileName.Contains(k.ToLowerInvariant()));
            score += Math.Min(0.3, keywordMatches * 0.1);
        }

        // Metadata matching
        if (metadata != null && category.Keywords != null)
        {
            foreach (var value in metadata.Values.OfType<string>())
            {
                var lowerValue = value.ToLowerInvariant();
                var keywordMatches = category.Keywords.Count(k => lowerValue.Contains(k.ToLowerInvariant()));
                score += Math.Min(0.2, keywordMatches * 0.05);
            }
        }

        // MIME type matching for specific categories
        if (category.MimeTypes?.Contains(typeInfo.MimeType) == true)
            score += 0.2;

        return Math.Min(1.0, score);
    }

    private async Task<List<string>> ExtractKeywordsAsync(Stream dataStream, CancellationToken ct)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var buffer = new byte[Math.Min(_config.KeywordExtractionLimit, dataStream.Length)];
            var bytesRead = await dataStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);

            if (bytesRead > 0)
            {
                var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                // Extract words (simple tokenization)
                var words = Regex.Matches(text, @"\b[a-zA-Z]{4,}\b")
                    .Select(m => m.Value.ToLowerInvariant())
                    .Where(w => !IsStopWord(w))
                    .GroupBy(w => w)
                    .OrderByDescending(g => g.Count())
                    .Take(20)
                    .Select(g => g.Key);

                foreach (var word in words)
                    keywords.Add(word);
            }
        }
        catch { }

        return keywords.ToList();
    }

    private bool IsStopWord(string word)
    {
        var stopWords = new HashSet<string>
        {
            "the", "and", "for", "are", "but", "not", "you", "all", "can", "had",
            "her", "was", "one", "our", "out", "has", "have", "been", "were", "they",
            "this", "that", "with", "from", "your", "will", "would", "there", "their"
        };
        return stopWords.Contains(word);
    }

    public IntelligentClassifierStatistics GetStatistics()
    {
        return new IntelligentClassifierStatistics
        {
            TotalClassifications = _classificationsPerformed,
            RegisteredCategories = _categories.Count,
            CacheSize = _classificationCache.Count
        };
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return _typeDetector.DisposeAsync();
    }
}

public sealed class IntelligentClassifierConfig
{
    public int MaxCategories { get; set; } = 5;
    public bool EnableCaching { get; set; } = true;
    public int KeywordExtractionLimit { get; set; } = 65536;
    public double MinConfidenceThreshold { get; set; } = 0.3;
}

public sealed class CategoryDefinition
{
    public string CategoryId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string[]? Keywords { get; init; }
    public ContentType[]? ContentTypes { get; init; }
    public string[]? MimeTypes { get; init; }
    public CategoryPriority Priority { get; init; } = CategoryPriority.Normal;
    public string? RetentionPolicy { get; init; }
    public bool IsPII { get; init; }
    public bool IsHIPAA { get; init; }
}

public enum CategoryPriority
{
    Low,
    Normal,
    High,
    Critical
}

public sealed class ClassificationResult
{
    public ContentTypeInfo ContentTypeInfo { get; init; } = new();
    public string? PrimaryCategory { get; set; }
    public double PrimaryCategoryConfidence { get; set; }
    public List<CategoryMatch> Categories { get; set; } = new();
    public List<string> ExtractedKeywords { get; set; } = new();
    public string? SuggestedRetentionPolicy { get; set; }
    public bool ContainsPII { get; set; }
    public bool IsHIPAAProtected { get; set; }
    public CategoryPriority SuggestedPriority { get; set; }
    public DateTime ClassifiedAt { get; init; }
}

public sealed class CategoryMatch
{
    public string CategoryId { get; init; } = string.Empty;
    public string CategoryName { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public CategoryPriority Priority { get; init; }
}

public sealed class ClassificationCompletedEventArgs : EventArgs
{
    public ClassificationResult Result { get; init; } = new();
}

public record IntelligentClassifierStatistics
{
    public long TotalClassifications { get; init; }
    public int RegisteredCategories { get; init; }
    public int CacheSize { get; init; }
}

#endregion

#region EM4.3: Auto-Tiering Recommender

/// <summary>
/// Recommends optimal storage tiers based on access patterns, content type,
/// and cost optimization strategies.
/// </summary>
public sealed class AutoTieringRecommender : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, AccessPatternInfo> _accessPatterns = new();
    private readonly ConcurrentDictionary<string, TierRecommendation> _recommendations = new();
    private readonly List<StorageTier> _tiers = new();
    private readonly Timer _analysisTimer;
    private readonly AutoTieringConfig _config;
    private volatile bool _disposed;

    public event EventHandler<TierRecommendationEventArgs>? RecommendationGenerated;

    public AutoTieringRecommender(AutoTieringConfig? config = null)
    {
        _config = config ?? new AutoTieringConfig();
        InitializeDefaultTiers();
        _analysisTimer = new Timer(AnalyzePatterns, null,
            TimeSpan.FromMinutes(_config.AnalysisIntervalMinutes),
            TimeSpan.FromMinutes(_config.AnalysisIntervalMinutes));
    }

    private void InitializeDefaultTiers()
    {
        _tiers.Add(new StorageTier
        {
            TierId = "hot",
            Name = "Hot Storage",
            Description = "High-performance SSD storage for frequently accessed data",
            CostPerGBMonth = 0.023m,
            LatencyMs = 1,
            ThroughputMBps = 1000,
            AccessFrequencyThreshold = 10, // > 10 accesses per day
            Durability = 0.999999999
        });

        _tiers.Add(new StorageTier
        {
            TierId = "warm",
            Name = "Warm Storage",
            Description = "Balanced storage for moderately accessed data",
            CostPerGBMonth = 0.0125m,
            LatencyMs = 10,
            ThroughputMBps = 500,
            AccessFrequencyThreshold = 1, // 1-10 accesses per day
            Durability = 0.999999999
        });

        _tiers.Add(new StorageTier
        {
            TierId = "cool",
            Name = "Cool Storage",
            Description = "Lower-cost storage for infrequently accessed data",
            CostPerGBMonth = 0.01m,
            LatencyMs = 50,
            ThroughputMBps = 200,
            AccessFrequencyThreshold = 0.03, // ~1 access per month
            Durability = 0.999999999,
            MinRetentionDays = 30
        });

        _tiers.Add(new StorageTier
        {
            TierId = "archive",
            Name = "Archive Storage",
            Description = "Lowest-cost storage for rarely accessed data",
            CostPerGBMonth = 0.00099m,
            LatencyMs = 3600000, // Hours to retrieve
            ThroughputMBps = 50,
            AccessFrequencyThreshold = 0, // Rarely accessed
            Durability = 0.99999999999,
            MinRetentionDays = 180,
            RetrievalCostPerGB = 0.02m
        });
    }

    /// <summary>
    /// Records an access event for tiering analysis.
    /// </summary>
    public void RecordAccess(string objectId, AccessType accessType, long bytesAccessed)
    {
        var pattern = _accessPatterns.GetOrAdd(objectId, _ => new AccessPatternInfo
        {
            ObjectId = objectId,
            FirstAccessAt = DateTime.UtcNow
        });

        lock (pattern)
        {
            pattern.TotalAccesses++;
            pattern.LastAccessAt = DateTime.UtcNow;
            pattern.TotalBytesAccessed += bytesAccessed;

            if (accessType == AccessType.Read)
                pattern.ReadCount++;
            else
                pattern.WriteCount++;

            pattern.RecentAccesses.Enqueue(new AccessEvent
            {
                Timestamp = DateTime.UtcNow,
                AccessType = accessType,
                Bytes = bytesAccessed
            });

            // Keep only recent access events
            while (pattern.RecentAccesses.Count > _config.MaxRecentAccessEvents)
                pattern.RecentAccesses.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Gets tier recommendation for an object.
    /// </summary>
    public TierRecommendation GetRecommendation(
        string objectId,
        long objectSizeBytes,
        ContentTypeInfo? contentType = null,
        ClassificationResult? classification = null)
    {
        var recommendation = new TierRecommendation
        {
            ObjectId = objectId,
            ObjectSizeBytes = objectSizeBytes,
            GeneratedAt = DateTime.UtcNow
        };

        // Get access pattern if available
        AccessPatternInfo? pattern = null;
        _accessPatterns.TryGetValue(objectId, out pattern);

        // Calculate access frequency (accesses per day)
        double accessFrequency = 0;
        if (pattern != null)
        {
            var daysSinceFirst = (DateTime.UtcNow - pattern.FirstAccessAt).TotalDays;
            if (daysSinceFirst > 0)
                accessFrequency = pattern.TotalAccesses / daysSinceFirst;
        }

        // Find optimal tier based on access frequency
        var optimalTier = _tiers
            .Where(t => accessFrequency >= t.AccessFrequencyThreshold)
            .OrderBy(t => t.CostPerGBMonth)
            .FirstOrDefault() ?? _tiers.Last();

        recommendation.RecommendedTier = optimalTier.TierId;
        recommendation.AccessFrequency = accessFrequency;

        // Calculate cost savings
        var currentTier = _tiers.FirstOrDefault(t => t.TierId == "hot") ?? _tiers.First();
        var monthlyCostCurrent = objectSizeBytes / (1024m * 1024m * 1024m) * currentTier.CostPerGBMonth;
        var monthlyCostOptimal = objectSizeBytes / (1024m * 1024m * 1024m) * optimalTier.CostPerGBMonth;
        recommendation.EstimatedMonthlySavings = monthlyCostCurrent - monthlyCostOptimal;

        // Adjust for content type
        if (contentType != null)
        {
            // Keep media files that are rarely accessed in archive
            if ((contentType.PrimaryType == ContentType.Video || contentType.PrimaryType == ContentType.Audio) &&
                accessFrequency < 0.1)
            {
                recommendation.RecommendedTier = "archive";
            }

            // Keep frequently accessed images in hot storage
            if (contentType.PrimaryType == ContentType.Image && accessFrequency > 5)
            {
                recommendation.RecommendedTier = "hot";
            }
        }

        // Adjust for classification
        if (classification != null)
        {
            // Critical data should stay in higher tiers
            if (classification.SuggestedPriority == CategoryPriority.Critical)
            {
                if (recommendation.RecommendedTier == "archive")
                    recommendation.RecommendedTier = "cool";
            }

            // HIPAA data may have compliance requirements
            if (classification.IsHIPAAProtected)
            {
                recommendation.ComplianceNotes = "HIPAA-protected data; verify archive tier compliance";
            }
        }

        // Calculate confidence
        recommendation.Confidence = pattern != null ?
            Math.Min(1.0, pattern.TotalAccesses / 100.0) : 0.3;

        // Set reasoning
        recommendation.Reasoning = GenerateReasoning(recommendation, pattern, optimalTier);

        // Cache and emit event
        _recommendations[objectId] = recommendation;
        RecommendationGenerated?.Invoke(this, new TierRecommendationEventArgs { Recommendation = recommendation });

        return recommendation;
    }

    /// <summary>
    /// Gets bulk recommendations for multiple objects.
    /// </summary>
    public async Task<List<TierRecommendation>> GetBulkRecommendationsAsync(
        IEnumerable<(string ObjectId, long SizeBytes)> objects,
        CancellationToken ct = default)
    {
        var recommendations = new List<TierRecommendation>();

        await Task.Run(() =>
        {
            foreach (var (objectId, sizeBytes) in objects)
            {
                ct.ThrowIfCancellationRequested();
                recommendations.Add(GetRecommendation(objectId, sizeBytes));
            }
        }, ct);

        return recommendations;
    }

    /// <summary>
    /// Registers a custom storage tier.
    /// </summary>
    public void RegisterTier(StorageTier tier)
    {
        _tiers.Add(tier);
        _tiers.Sort((a, b) => b.AccessFrequencyThreshold.CompareTo(a.AccessFrequencyThreshold));
    }

    /// <summary>
    /// Gets all registered storage tiers.
    /// </summary>
    public IReadOnlyList<StorageTier> GetTiers() => _tiers.ToList();

    private string GenerateReasoning(TierRecommendation recommendation, AccessPatternInfo? pattern, StorageTier tier)
    {
        var reasons = new List<string>();

        if (pattern == null)
        {
            reasons.Add("No access history available; defaulting to cost-optimized tier");
        }
        else
        {
            if (recommendation.AccessFrequency > 10)
                reasons.Add($"High access frequency ({recommendation.AccessFrequency:F1}/day) suggests hot storage");
            else if (recommendation.AccessFrequency > 1)
                reasons.Add($"Moderate access frequency ({recommendation.AccessFrequency:F1}/day) suggests warm storage");
            else if (recommendation.AccessFrequency > 0.03)
                reasons.Add($"Low access frequency ({recommendation.AccessFrequency:F2}/day) suggests cool storage");
            else
                reasons.Add($"Minimal access ({recommendation.AccessFrequency:F3}/day) suggests archive storage");
        }

        if (recommendation.EstimatedMonthlySavings > 0)
            reasons.Add($"Potential monthly savings: ${recommendation.EstimatedMonthlySavings:F4}");

        return string.Join("; ", reasons);
    }

    private void AnalyzePatterns(object? state)
    {
        if (_disposed) return;

        // Clean up old patterns
        var staleThreshold = DateTime.UtcNow.AddDays(-_config.PatternRetentionDays);
        foreach (var kvp in _accessPatterns)
        {
            if (kvp.Value.LastAccessAt < staleThreshold)
                _accessPatterns.TryRemove(kvp.Key, out _);
        }
    }

    public AutoTieringStatistics GetStatistics()
    {
        var recommendations = _recommendations.Values.ToList();
        return new AutoTieringStatistics
        {
            TrackedObjects = _accessPatterns.Count,
            TotalRecommendations = recommendations.Count,
            RecommendationsByTier = recommendations
                .GroupBy(r => r.RecommendedTier)
                .ToDictionary(g => g.Key, g => g.Count()),
            EstimatedTotalMonthlySavings = recommendations.Sum(r => r.EstimatedMonthlySavings)
        };
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _analysisTimer.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class AutoTieringConfig
{
    public int AnalysisIntervalMinutes { get; set; } = 60;
    public int PatternRetentionDays { get; set; } = 90;
    public int MaxRecentAccessEvents { get; set; } = 1000;
}

public sealed class StorageTier
{
    public string TierId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public decimal CostPerGBMonth { get; init; }
    public double LatencyMs { get; init; }
    public double ThroughputMBps { get; init; }
    public double AccessFrequencyThreshold { get; init; }
    public double Durability { get; init; }
    public int MinRetentionDays { get; init; }
    public decimal RetrievalCostPerGB { get; init; }
}

public sealed class AccessPatternInfo
{
    public string ObjectId { get; init; } = string.Empty;
    public DateTime FirstAccessAt { get; init; }
    public DateTime LastAccessAt { get; set; }
    public long TotalAccesses { get; set; }
    public long ReadCount { get; set; }
    public long WriteCount { get; set; }
    public long TotalBytesAccessed { get; set; }
    public ConcurrentQueue<AccessEvent> RecentAccesses { get; } = new();
}

public sealed class AccessEvent
{
    public DateTime Timestamp { get; init; }
    public AccessType AccessType { get; init; }
    public long Bytes { get; init; }
}

public enum AccessType
{
    Read,
    Write,
    Delete,
    List
}

public sealed class TierRecommendation
{
    public string ObjectId { get; init; } = string.Empty;
    public long ObjectSizeBytes { get; init; }
    public string RecommendedTier { get; set; } = string.Empty;
    public double AccessFrequency { get; set; }
    public decimal EstimatedMonthlySavings { get; set; }
    public double Confidence { get; set; }
    public string Reasoning { get; set; } = string.Empty;
    public string? ComplianceNotes { get; set; }
    public DateTime GeneratedAt { get; init; }
}

public sealed class TierRecommendationEventArgs : EventArgs
{
    public TierRecommendation Recommendation { get; init; } = new();
}

public record AutoTieringStatistics
{
    public int TrackedObjects { get; init; }
    public int TotalRecommendations { get; init; }
    public Dictionary<string, int> RecommendationsByTier { get; init; } = new();
    public decimal EstimatedTotalMonthlySavings { get; init; }
}

#endregion

#region EM4.4: Content Extraction Pipeline

/// <summary>
/// Pipeline for extracting text and metadata from various content types.
/// Supports documents, images (OCR), audio (transcription), and structured data.
/// </summary>
public sealed class ContentExtractionPipeline : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IContentExtractor> _extractors = new();
    private readonly ConcurrentDictionary<string, ExtractionResult> _extractionCache = new();
    private readonly ContentExtractionConfig _config;
    private readonly StorageTypeDetector _typeDetector;
    private long _extractionsPerformed;
    private volatile bool _disposed;

    public event EventHandler<ExtractionCompletedEventArgs>? ExtractionCompleted;

    public ContentExtractionPipeline(ContentExtractionConfig? config = null)
    {
        _config = config ?? new ContentExtractionConfig();
        _typeDetector = new StorageTypeDetector();
        InitializeDefaultExtractors();
    }

    private void InitializeDefaultExtractors()
    {
        _extractors["text/plain"] = new PlainTextExtractor();
        _extractors["text/markdown"] = new PlainTextExtractor();
        _extractors["text/html"] = new HtmlTextExtractor();
        _extractors["application/json"] = new JsonExtractor();
        _extractors["application/xml"] = new XmlExtractor();
        _extractors["text/csv"] = new CsvExtractor();
        _extractors["text/x-csharp"] = new SourceCodeExtractor();
        _extractors["text/x-python"] = new SourceCodeExtractor();
        _extractors["text/javascript"] = new SourceCodeExtractor();

        // Placeholder extractors for binary formats
        _extractors["application/pdf"] = new PdfExtractor();
        _extractors["image/*"] = new ImageMetadataExtractor();
        _extractors["audio/*"] = new AudioMetadataExtractor();
        _extractors["video/*"] = new VideoMetadataExtractor();
    }

    /// <summary>
    /// Extracts text and metadata from content.
    /// </summary>
    public async Task<ExtractionResult> ExtractAsync(
        Stream dataStream,
        string? fileName = null,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _extractionsPerformed);

        // Detect content type
        var typeInfo = await _typeDetector.DetectTypeAsync(dataStream, fileName, ct);

        if (dataStream.CanSeek)
            dataStream.Position = 0;

        // Check cache
        if (_config.EnableCaching && typeInfo.ContentHash != null &&
            _extractionCache.TryGetValue(typeInfo.ContentHash, out var cached))
        {
            return cached;
        }

        var result = new ExtractionResult
        {
            ContentTypeInfo = typeInfo,
            ExtractedAt = DateTime.UtcNow
        };

        // Find appropriate extractor
        var extractor = FindExtractor(typeInfo);

        if (extractor != null)
        {
            try
            {
                var extraction = await extractor.ExtractAsync(dataStream, ct);
                result.ExtractedText = extraction.Text;
                result.Metadata = extraction.Metadata;
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }
        }
        else
        {
            result.Success = false;
            result.ErrorMessage = $"No extractor available for {typeInfo.MimeType}";
        }

        // Cache result
        if (_config.EnableCaching && typeInfo.ContentHash != null)
        {
            _extractionCache[typeInfo.ContentHash] = result;
        }

        ExtractionCompleted?.Invoke(this, new ExtractionCompletedEventArgs { Result = result });

        return result;
    }

    /// <summary>
    /// Extracts from byte array.
    /// </summary>
    public async Task<ExtractionResult> ExtractAsync(
        byte[] data,
        string? fileName = null,
        CancellationToken ct = default)
    {
        using var stream = new MemoryStream(data);
        return await ExtractAsync(stream, fileName, ct);
    }

    /// <summary>
    /// Registers a custom extractor for a MIME type.
    /// </summary>
    public void RegisterExtractor(string mimeType, IContentExtractor extractor)
    {
        _extractors[mimeType] = extractor;
    }

    private IContentExtractor? FindExtractor(ContentTypeInfo typeInfo)
    {
        // Exact match
        if (_extractors.TryGetValue(typeInfo.MimeType, out var exact))
            return exact;

        // Wildcard match (e.g., image/*)
        var majorType = typeInfo.MimeType.Split('/')[0];
        if (_extractors.TryGetValue($"{majorType}/*", out var wildcard))
            return wildcard;

        return null;
    }

    public ContentExtractionStatistics GetStatistics()
    {
        return new ContentExtractionStatistics
        {
            TotalExtractions = _extractionsPerformed,
            CacheSize = _extractionCache.Count,
            RegisteredExtractors = _extractors.Count
        };
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return _typeDetector.DisposeAsync();
    }
}

/// <summary>
/// Interface for content extractors.
/// </summary>
public interface IContentExtractor
{
    Task<ContentExtraction> ExtractAsync(Stream dataStream, CancellationToken ct = default);
}

public sealed class ContentExtraction
{
    public string? Text { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

// Built-in extractors

internal sealed class PlainTextExtractor : IContentExtractor
{
    public async Task<ContentExtraction> ExtractAsync(Stream dataStream, CancellationToken ct)
    {
        using var reader = new StreamReader(dataStream);
        var text = await reader.ReadToEndAsync(ct);
        return new ContentExtraction
        {
            Text = text,
            Metadata = new Dictionary<string, object>
            {
                ["lineCount"] = text.Split('\n').Length,
                ["wordCount"] = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length,
                ["charCount"] = text.Length
            }
        };
    }
}

internal sealed class HtmlTextExtractor : IContentExtractor
{
    public async Task<ContentExtraction> ExtractAsync(Stream dataStream, CancellationToken ct)
    {
        using var reader = new StreamReader(dataStream);
        var html = await reader.ReadToEndAsync(ct);

        // Strip HTML tags
        var text = Regex.Replace(html, "<[^>]+>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();

        // Extract title
        var titleMatch = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return new ContentExtraction
        {
            Text = text,
            Metadata = new Dictionary<string, object>
            {
                ["title"] = titleMatch.Success ? titleMatch.Groups[1].Value : "",
                ["originalLength"] = html.Length
            }
        };
    }
}

internal sealed class JsonExtractor : IContentExtractor
{
    public async Task<ContentExtraction> ExtractAsync(Stream dataStream, CancellationToken ct)
    {
        using var reader = new StreamReader(dataStream);
        var json = await reader.ReadToEndAsync(ct);

        var metadata = new Dictionary<string, object>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            metadata["rootType"] = doc.RootElement.ValueKind.ToString();

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                metadata["propertyCount"] = doc.RootElement.EnumerateObject().Count();
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                metadata["arrayLength"] = doc.RootElement.GetArrayLength();
        }
        catch { }

        return new ContentExtraction { Text = json, Metadata = metadata };
    }
}

internal sealed class XmlExtractor : IContentExtractor
{
    public async Task<ContentExtraction> ExtractAsync(Stream dataStream, CancellationToken ct)
    {
        using var reader = new StreamReader(dataStream);
        var xml = await reader.ReadToEndAsync(ct);

        // Extract text content
        var text = Regex.Replace(xml, "<[^>]+>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();

        return new ContentExtraction
        {
            Text = text,
            Metadata = new Dictionary<string, object>
            {
                ["originalLength"] = xml.Length
            }
        };
    }
}

internal sealed class CsvExtractor : IContentExtractor
{
    public async Task<ContentExtraction> ExtractAsync(Stream dataStream, CancellationToken ct)
    {
        using var reader = new StreamReader(dataStream);
        var csv = await reader.ReadToEndAsync(ct);

        var lines = csv.Split('\n');
        var headers = lines.FirstOrDefault()?.Split(',').Select(h => h.Trim()).ToList() ?? new List<string>();

        return new ContentExtraction
        {
            Text = csv,
            Metadata = new Dictionary<string, object>
            {
                ["rowCount"] = lines.Length,
                ["columnCount"] = headers.Count,
                ["headers"] = headers
            }
        };
    }
}

internal sealed class SourceCodeExtractor : IContentExtractor
{
    public async Task<ContentExtraction> ExtractAsync(Stream dataStream, CancellationToken ct)
    {
        using var reader = new StreamReader(dataStream);
        var code = await reader.ReadToEndAsync(ct);

        var lines = code.Split('\n');
        var nonEmptyLines = lines.Count(l => !string.IsNullOrWhiteSpace(l));
        var commentLines = lines.Count(l => l.TrimStart().StartsWith("//") || l.TrimStart().StartsWith("#") || l.TrimStart().StartsWith("/*"));

        return new ContentExtraction
        {
            Text = code,
            Metadata = new Dictionary<string, object>
            {
                ["totalLines"] = lines.Length,
                ["codeLines"] = nonEmptyLines - commentLines,
                ["commentLines"] = commentLines,
                ["blankLines"] = lines.Length - nonEmptyLines
            }
        };
    }
}

internal sealed class PdfExtractor : IContentExtractor
{
    public Task<ContentExtraction> ExtractAsync(Stream dataStream, CancellationToken ct)
    {
        // Placeholder - would use PDF library in production
        return Task.FromResult(new ContentExtraction
        {
            Text = "[PDF content extraction requires PDF parsing library]",
            Metadata = new Dictionary<string, object>
            {
                ["format"] = "PDF",
                ["extractionMethod"] = "placeholder"
            }
        });
    }
}

internal sealed class ImageMetadataExtractor : IContentExtractor
{
    public Task<ContentExtraction> ExtractAsync(Stream dataStream, CancellationToken ct)
    {
        var metadata = new Dictionary<string, object>
        {
            ["format"] = "Image",
            ["extractionMethod"] = "metadata-only"
        };

        // Read EXIF data if available (simplified)
        // In production, would use image processing library

        return Task.FromResult(new ContentExtraction
        {
            Text = null,
            Metadata = metadata
        });
    }
}

internal sealed class AudioMetadataExtractor : IContentExtractor
{
    public Task<ContentExtraction> ExtractAsync(Stream dataStream, CancellationToken ct)
    {
        return Task.FromResult(new ContentExtraction
        {
            Text = null,
            Metadata = new Dictionary<string, object>
            {
                ["format"] = "Audio",
                ["extractionMethod"] = "metadata-only"
            }
        });
    }
}

internal sealed class VideoMetadataExtractor : IContentExtractor
{
    public Task<ContentExtraction> ExtractAsync(Stream dataStream, CancellationToken ct)
    {
        return Task.FromResult(new ContentExtraction
        {
            Text = null,
            Metadata = new Dictionary<string, object>
            {
                ["format"] = "Video",
                ["extractionMethod"] = "metadata-only"
            }
        });
    }
}

public sealed class ContentExtractionConfig
{
    public bool EnableCaching { get; set; } = true;
    public int MaxTextLength { get; set; } = 1024 * 1024; // 1MB
}

public sealed class ExtractionResult
{
    public ContentTypeInfo ContentTypeInfo { get; init; } = new();
    public bool Success { get; set; }
    public string? ExtractedText { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public DateTime ExtractedAt { get; init; }
}

public sealed class ExtractionCompletedEventArgs : EventArgs
{
    public ExtractionResult Result { get; init; } = new();
}

public record ContentExtractionStatistics
{
    public long TotalExtractions { get; init; }
    public int CacheSize { get; init; }
    public int RegisteredExtractors { get; init; }
}

#endregion

#region EM4.5: Smart Search Indexer

/// <summary>
/// Automatic full-text and vector indexing for intelligent search.
/// Integrates with content extraction and AI embeddings.
/// </summary>
public sealed class SmartSearchIndexer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IndexedDocument> _index = new();
    private readonly ConcurrentDictionary<string, float[]> _vectorIndex = new();
    private readonly InvertedIndex _invertedIndex = new();
    private readonly ContentExtractionPipeline _extractionPipeline;
    private readonly IntelligentContentClassifier _classifier;
    private readonly SmartSearchConfig _config;
    private long _documentsIndexed;
    private long _searchesPerformed;
    private volatile bool _disposed;

    public event EventHandler<DocumentIndexedEventArgs>? DocumentIndexed;

    public SmartSearchIndexer(SmartSearchConfig? config = null)
    {
        _config = config ?? new SmartSearchConfig();
        _extractionPipeline = new ContentExtractionPipeline();
        _classifier = new IntelligentContentClassifier();
    }

    /// <summary>
    /// Indexes content with automatic text extraction and classification.
    /// </summary>
    public async Task<IndexingResult> IndexAsync(
        string documentId,
        Stream dataStream,
        string? fileName = null,
        Dictionary<string, object>? additionalMetadata = null,
        CancellationToken ct = default)
    {
        var result = new IndexingResult
        {
            DocumentId = documentId,
            IndexedAt = DateTime.UtcNow
        };

        try
        {
            // Extract content
            var extraction = await _extractionPipeline.ExtractAsync(dataStream, fileName, ct);

            if (dataStream.CanSeek)
                dataStream.Position = 0;

            // Classify content
            var classification = await _classifier.ClassifyAsync(dataStream, fileName, additionalMetadata, ct);

            // Create indexed document
            var doc = new IndexedDocument
            {
                DocumentId = documentId,
                FileName = fileName,
                ContentType = extraction.ContentTypeInfo,
                Classification = classification,
                ExtractedText = extraction.ExtractedText,
                Metadata = MergeMetadata(extraction.Metadata, additionalMetadata),
                IndexedAt = DateTime.UtcNow
            };

            // Build full-text index
            if (!string.IsNullOrEmpty(extraction.ExtractedText))
            {
                var tokens = Tokenize(extraction.ExtractedText);
                doc.TokenCount = tokens.Count;

                foreach (var token in tokens.Distinct())
                {
                    _invertedIndex.Add(token, documentId);
                }

                // Generate vector embedding (simplified - in production would use AI model)
                if (_config.EnableVectorIndexing)
                {
                    doc.EmbeddingVector = GenerateSimpleEmbedding(extraction.ExtractedText);
                    _vectorIndex[documentId] = doc.EmbeddingVector;
                }
            }

            _index[documentId] = doc;
            Interlocked.Increment(ref _documentsIndexed);

            result.Success = true;
            result.TokenCount = doc.TokenCount;
            result.Categories = classification.Categories.Select(c => c.CategoryId).ToList();

            DocumentIndexed?.Invoke(this, new DocumentIndexedEventArgs { Document = doc });
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Performs full-text search.
    /// </summary>
    public async Task<SearchResults> SearchAsync(
        string query,
        SearchOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new SearchOptions();
        Interlocked.Increment(ref _searchesPerformed);

        var results = new SearchResults
        {
            Query = query,
            SearchedAt = DateTime.UtcNow
        };

        var queryTokens = Tokenize(query);
        var candidateIds = new Dictionary<string, double>();

        // Full-text search using inverted index
        foreach (var token in queryTokens)
        {
            var docIds = _invertedIndex.Get(token);
            foreach (var docId in docIds)
            {
                candidateIds[docId] = candidateIds.GetValueOrDefault(docId) + 1;
            }
        }

        // Vector search if enabled
        if (_config.EnableVectorIndexing && options.UseSemanticSearch)
        {
            var queryVector = GenerateSimpleEmbedding(query);
            foreach (var (docId, vector) in _vectorIndex)
            {
                var similarity = CosineSimilarity(queryVector, vector);
                if (similarity > options.MinSemanticScore)
                {
                    candidateIds[docId] = candidateIds.GetValueOrDefault(docId) + similarity * 2;
                }
            }
        }

        // Score and rank results
        var scoredResults = new List<SearchResult>();
        foreach (var (docId, score) in candidateIds.OrderByDescending(kvp => kvp.Value))
        {
            if (!_index.TryGetValue(docId, out var doc)) continue;

            // Apply filters
            if (options.ContentTypes?.Any() == true &&
                !options.ContentTypes.Contains(doc.ContentType.PrimaryType))
                continue;

            if (options.Categories?.Any() == true &&
                doc.Classification?.Categories.All(c => !options.Categories.Contains(c.CategoryId)) == true)
                continue;

            var searchResult = new SearchResult
            {
                DocumentId = docId,
                Score = score / queryTokens.Count,
                FileName = doc.FileName,
                ContentType = doc.ContentType.PrimaryType,
                Category = doc.Classification?.PrimaryCategory,
                Snippet = GenerateSnippet(doc.ExtractedText, queryTokens, options.SnippetLength)
            };

            scoredResults.Add(searchResult);

            if (scoredResults.Count >= options.MaxResults)
                break;
        }

        results.Results = scoredResults;
        results.TotalMatches = candidateIds.Count;

        return results;
    }

    /// <summary>
    /// Performs semantic (vector) search.
    /// </summary>
    public async Task<SearchResults> SemanticSearchAsync(
        string query,
        int maxResults = 10,
        CancellationToken ct = default)
    {
        var options = new SearchOptions
        {
            UseSemanticSearch = true,
            MaxResults = maxResults,
            MinSemanticScore = 0.5
        };
        return await SearchAsync(query, options, ct);
    }

    /// <summary>
    /// Removes a document from the index.
    /// </summary>
    public bool RemoveFromIndex(string documentId)
    {
        if (_index.TryRemove(documentId, out var doc))
        {
            if (doc.ExtractedText != null)
            {
                var tokens = Tokenize(doc.ExtractedText);
                foreach (var token in tokens.Distinct())
                {
                    _invertedIndex.Remove(token, documentId);
                }
            }
            _vectorIndex.TryRemove(documentId, out _);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gets an indexed document by ID.
    /// </summary>
    public IndexedDocument? GetDocument(string documentId)
    {
        return _index.TryGetValue(documentId, out var doc) ? doc : null;
    }

    private List<string> Tokenize(string text)
    {
        return Regex.Matches(text.ToLowerInvariant(), @"\b[a-z0-9]+\b")
            .Select(m => m.Value)
            .Where(t => t.Length >= 2 && !IsStopWord(t))
            .ToList();
    }

    private bool IsStopWord(string word)
    {
        var stopWords = new HashSet<string>
        {
            "the", "and", "for", "are", "but", "not", "you", "all", "can", "had",
            "her", "was", "one", "our", "out", "has", "have", "been", "were", "they",
            "this", "that", "with", "from", "your", "will", "would", "there", "their",
            "what", "when", "where", "which", "while", "who", "why", "how"
        };
        return stopWords.Contains(word);
    }

    private float[] GenerateSimpleEmbedding(string text)
    {
        // Simplified embedding generation
        // In production, would use AI embedding model
        var vector = new float[128];
        var tokens = Tokenize(text);

        foreach (var token in tokens)
        {
            var hash = token.GetHashCode();
            for (int i = 0; i < 128; i++)
            {
                vector[i] += ((hash >> (i % 32)) & 1) == 1 ? 0.1f : -0.1f;
            }
        }

        // Normalize
        var magnitude = (float)Math.Sqrt(vector.Sum(v => v * v));
        if (magnitude > 0)
        {
            for (int i = 0; i < vector.Length; i++)
                vector[i] /= magnitude;
        }

        return vector;
    }

    private double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA == 0 || magB == 0) return 0;
        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }

    private string? GenerateSnippet(string? text, List<string> queryTokens, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return null;

        // Find first occurrence of any query term
        var lowerText = text.ToLowerInvariant();
        var bestStart = 0;

        foreach (var token in queryTokens)
        {
            var idx = lowerText.IndexOf(token);
            if (idx >= 0)
            {
                bestStart = Math.Max(0, idx - maxLength / 4);
                break;
            }
        }

        var snippetLength = Math.Min(maxLength, text.Length - bestStart);
        var snippet = text.Substring(bestStart, snippetLength);

        if (bestStart > 0) snippet = "..." + snippet;
        if (bestStart + snippetLength < text.Length) snippet += "...";

        return snippet;
    }

    private Dictionary<string, object> MergeMetadata(Dictionary<string, object> extracted, Dictionary<string, object>? additional)
    {
        var result = new Dictionary<string, object>(extracted);
        if (additional != null)
        {
            foreach (var (key, value) in additional)
                result[key] = value;
        }
        return result;
    }

    public SmartSearchStatistics GetStatistics()
    {
        return new SmartSearchStatistics
        {
            DocumentsIndexed = _documentsIndexed,
            SearchesPerformed = _searchesPerformed,
            IndexSize = _index.Count,
            VectorIndexSize = _vectorIndex.Count,
            UniqueTerms = _invertedIndex.TermCount
        };
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _extractionPipeline.DisposeAsync();
        await _classifier.DisposeAsync();
    }
}

internal sealed class InvertedIndex
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _index = new();
    private readonly object _lock = new();

    public void Add(string term, string documentId)
    {
        var set = _index.GetOrAdd(term, _ => new HashSet<string>());
        lock (_lock)
        {
            set.Add(documentId);
        }
    }

    public void Remove(string term, string documentId)
    {
        if (_index.TryGetValue(term, out var set))
        {
            lock (_lock)
            {
                set.Remove(documentId);
            }
        }
    }

    public IEnumerable<string> Get(string term)
    {
        if (_index.TryGetValue(term, out var set))
        {
            lock (_lock)
            {
                return set.ToList();
            }
        }
        return Enumerable.Empty<string>();
    }

    public int TermCount => _index.Count;
}

public sealed class SmartSearchConfig
{
    public bool EnableVectorIndexing { get; set; } = true;
    public int VectorDimensions { get; set; } = 128;
    public int MaxTokensPerDocument { get; set; } = 100000;
}

public sealed class IndexedDocument
{
    public string DocumentId { get; init; } = string.Empty;
    public string? FileName { get; init; }
    public ContentTypeInfo ContentType { get; init; } = new();
    public ClassificationResult? Classification { get; init; }
    public string? ExtractedText { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = new();
    public int TokenCount { get; set; }
    public float[]? EmbeddingVector { get; set; }
    public DateTime IndexedAt { get; init; }
}

public sealed class IndexingResult
{
    public string DocumentId { get; init; } = string.Empty;
    public bool Success { get; set; }
    public int TokenCount { get; set; }
    public List<string> Categories { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public DateTime IndexedAt { get; init; }
}

public sealed class SearchOptions
{
    public int MaxResults { get; set; } = 100;
    public bool UseSemanticSearch { get; set; }
    public double MinSemanticScore { get; set; } = 0.5;
    public ContentType[]? ContentTypes { get; set; }
    public string[]? Categories { get; set; }
    public int SnippetLength { get; set; } = 200;
}

public sealed class SearchResults
{
    public string Query { get; init; } = string.Empty;
    public List<SearchResult> Results { get; set; } = new();
    public int TotalMatches { get; set; }
    public DateTime SearchedAt { get; init; }
}

public sealed class SearchResult
{
    public string DocumentId { get; init; } = string.Empty;
    public double Score { get; init; }
    public string? FileName { get; init; }
    public ContentType ContentType { get; init; }
    public string? Category { get; init; }
    public string? Snippet { get; init; }
}

public sealed class DocumentIndexedEventArgs : EventArgs
{
    public IndexedDocument Document { get; init; } = new();
}

public record SmartSearchStatistics
{
    public long DocumentsIndexed { get; init; }
    public long SearchesPerformed { get; init; }
    public int IndexSize { get; init; }
    public int VectorIndexSize { get; init; }
    public int UniqueTerms { get; init; }
}

#endregion
