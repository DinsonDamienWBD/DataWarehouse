using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Compression;

/// <summary>
/// Content-aware compression strategy that analyzes content type to select the optimal algorithm.
/// Detects text, binary, image, structured, and executable content; selects Brotli for text,
/// ZLib for structured data, Deflate (fastest) for binary, and skips already-compressed content.
/// </summary>
internal sealed class ContentAwareCompressionStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compression-contentaware";

    /// <inheritdoc/>
    public override string Name => "Content-Aware Compression";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true,
        SupportsProjection = true,
        SupportsAggregation = true,
        SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average, AggregationType.Min, AggregationType.Max },
        MaxQueryComplexity = 7
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        var sourcePath = query.Source;

        if (!File.Exists(sourcePath))
            return MakeError("Source file not found", sourcePath, sw);

        var originalSize = new FileInfo(sourcePath).Length;
        var contentType = await DetectContentTypeAsync(sourcePath, ct);
        var selectedAlgorithm = SelectAlgorithm(contentType);

        if (selectedAlgorithm == "skip")
        {
            sw.Stop();
            return new ProcessingResult
            {
                Data = new Dictionary<string, object?>
                {
                    ["sourcePath"] = sourcePath, ["contentType"] = contentType,
                    ["selectedAlgorithm"] = "skip", ["reason"] = "Content already compressed",
                    ["originalSize"] = originalSize
                },
                Metadata = new ProcessingMetadata { RowsProcessed = 1, RowsReturned = 1, BytesProcessed = originalSize, ProcessingTimeMs = sw.Elapsed.TotalMilliseconds }
            };
        }

        var extension = selectedAlgorithm switch { "brotli" => ".br", "zlib" => ".zst", _ => ".deflate" };
        var outputPath = sourcePath + extension;

        await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        await using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            Stream compressor = selectedAlgorithm switch
            {
                "brotli" => new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true),
                "zlib" => new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true),
                _ => new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true)
            };

            await using (compressor)
            {
                await input.CopyToAsync(compressor, 81920, ct);
            }
        }

        var resultSize = new FileInfo(outputPath).Length;
        var ratio = originalSize > 0 ? (double)resultSize / originalSize : 1.0;
        sw.Stop();

        return new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["sourcePath"] = sourcePath, ["outputPath"] = outputPath,
                ["contentType"] = contentType, ["selectedAlgorithm"] = selectedAlgorithm,
                ["originalSize"] = originalSize, ["resultSize"] = resultSize,
                ["compressionRatio"] = Math.Round(ratio, 4),
                ["spaceSavings"] = Math.Round((1.0 - ratio) * 100.0, 2)
            },
            Metadata = new ProcessingMetadata
            {
                RowsProcessed = 1, RowsReturned = 1, BytesProcessed = originalSize,
                ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
            }
        };
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        if (!Directory.Exists(query.Source)) { await Task.CompletedTask; yield break; }

        var limit = query.Limit ?? int.MaxValue;
        var offset = query.Offset ?? 0;
        var idx = 0;
        foreach (var file in Directory.EnumerateFiles(query.Source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (idx < offset) { idx++; continue; }
            if (idx - offset >= limit) break;

            var info = new FileInfo(file);
            var type = await DetectContentTypeAsync(file, ct);
            var algo = SelectAlgorithm(type);

            yield return new ProcessingResult
            {
                Data = new Dictionary<string, object?>
                {
                    ["filePath"] = file, ["fileName"] = info.Name, ["size"] = info.Length,
                    ["contentType"] = type, ["recommendedAlgorithm"] = algo
                },
                Metadata = new ProcessingMetadata { RowsProcessed = 1, RowsReturned = 1, BytesProcessed = info.Length, ProcessingTimeMs = sw.Elapsed.TotalMilliseconds }
            };
            idx++;
        }
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CompressionAggregationHelper.AggregateFileSizes(query, aggregationType, ct);
    }

    /// <summary>
    /// Detects the content type by sampling the file and analyzing byte distribution.
    /// </summary>
    private static async Task<string> DetectContentTypeAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath)) return "unknown";

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        // Extension-based detection for known types
        if (ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".avif" or ".bmp" or ".tiff")
            return "image";
        if (ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".mp3" or ".aac" or ".ogg" or ".flac")
            return "media";
        if (ext is ".zip" or ".gz" or ".bz2" or ".xz" or ".zst" or ".lz4" or ".br" or ".rar" or ".7z")
            return "compressed";
        if (ext is ".json" or ".xml" or ".yaml" or ".yml" or ".csv" or ".tsv" or ".parquet" or ".avro")
            return "structured";
        if (ext is ".exe" or ".dll" or ".so" or ".dylib" or ".wasm")
            return "executable";

        // Byte-level analysis for unknown extensions
        var buffer = new byte[Math.Min(8192, new FileInfo(filePath).Length)];
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true);
        var read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
        if (read == 0) return "empty";

        // Calculate entropy and text ratio
        var textBytes = 0;
        var histogram = new int[256];
        for (var i = 0; i < read; i++)
        {
            histogram[buffer[i]]++;
            if (buffer[i] is (>= 0x20 and <= 0x7E) or 0x09 or 0x0A or 0x0D)
                textBytes++;
        }

        var textRatio = (double)textBytes / read;
        if (textRatio > 0.85) return "text";

        // Check Shannon entropy
        var entropy = 0.0;
        for (var i = 0; i < 256; i++)
        {
            if (histogram[i] == 0) continue;
            var p = (double)histogram[i] / read;
            entropy -= p * Math.Log2(p);
        }

        // High entropy suggests already-compressed or encrypted data
        if (entropy > 7.5) return "compressed";

        return "binary";
    }

    /// <summary>
    /// Selects the optimal compression algorithm based on content type.
    /// </summary>
    private static string SelectAlgorithm(string contentType) => contentType switch
    {
        "text" => "brotli",          // Best ratio for text content
        "structured" => "zlib",       // Good ratio for structured data
        "binary" => "deflate",        // Fast for generic binary
        "executable" => "deflate",    // Fast for executables
        "compressed" => "skip",       // Already compressed
        "media" => "skip",            // Already compressed media
        "image" => "skip",            // Already compressed images
        "empty" => "skip",            // Nothing to compress
        _ => "deflate"                // Default fast compression
    };

    private static ProcessingResult MakeError(string error, string source, Stopwatch sw)
    { sw.Stop(); return new ProcessingResult { Data = new Dictionary<string, object?> { ["error"] = error, ["source"] = source }, Metadata = new ProcessingMetadata { ProcessingTimeMs = sw.Elapsed.TotalMilliseconds } }; }
}
