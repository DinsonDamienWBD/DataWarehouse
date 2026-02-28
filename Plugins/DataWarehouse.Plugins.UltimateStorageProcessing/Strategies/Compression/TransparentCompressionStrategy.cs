using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Compression;

/// <summary>
/// Transparent compression strategy that auto-detects compression format from magic bytes.
/// Recognizes Zstd (0x28B52FFD), Gzip (0x1F8B), LZ4 (0x04224D18), and Brotli formats,
/// enabling seamless decompress-on-read and compress-on-write operations.
/// </summary>
internal sealed class TransparentCompressionStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compression-transparent";

    /// <inheritdoc/>
    public override string Name => "Transparent Compression";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true,
        SupportsProjection = true,
        SupportsAggregation = true,
        SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average, AggregationType.Min, AggregationType.Max },
        MaxQueryComplexity = 6
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
        var detectedFormat = await DetectCompressionFormatAsync(sourcePath, ct);
        var mode = GetOption<string>(query, "mode") ?? (detectedFormat != "none" ? "decompress" : "compress");
        var targetFormat = GetOption<string>(query, "targetFormat") ?? "gzip";

        string outputPath;
        if (mode == "decompress" && detectedFormat != "none")
        {
            outputPath = Path.ChangeExtension(sourcePath, null);
            if (outputPath == sourcePath) outputPath += ".decompressed";

            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            // Note: "zstd" and "lz4" magic bytes detected above are used for detection only.
            // Real decompression for zstd/lz4 requires native libraries not in .NET BCL.
            // Fall back to the underlying stream format that was actually used to create them.
            if (detectedFormat is "zstd" or "lz4")
            {
                throw new NotSupportedException(
                    $"Decompression of {detectedFormat.ToUpperInvariant()} format requires a native library " +
                    $"(ZstdSharp/ZstdNet for Zstd; K4os.Compression.LZ4 for LZ4). " +
                    $"Add the appropriate NuGet package reference.");
            }

            Stream decompressor = detectedFormat switch
            {
                "gzip" => new GZipStream(input, CompressionMode.Decompress),
                "brotli" => new BrotliStream(input, CompressionMode.Decompress),
                "zlib" => new ZLibStream(input, CompressionMode.Decompress),
                "deflate" => new DeflateStream(input, CompressionMode.Decompress),
                _ => new GZipStream(input, CompressionMode.Decompress)
            };

            await using (decompressor)
            {
                await decompressor.CopyToAsync(output, 81920, ct);
            }
        }
        else
        {
            // zstd and lz4 require native libraries not in .NET BCL
            if (targetFormat is "zstd" or "lz4")
            {
                throw new NotSupportedException(
                    $"Compression to {targetFormat.ToUpperInvariant()} format requires a native library " +
                    $"(ZstdSharp/ZstdNet for Zstd; K4os.Compression.LZ4 for LZ4). " +
                    $"Add the appropriate NuGet package reference.");
            }

            var extension = targetFormat switch { "brotli" => ".br", "gzip" => ".gz", "deflate" => ".deflate", _ => ".gz" };
            outputPath = sourcePath + extension;

            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            Stream compressor = targetFormat switch
            {
                "brotli" => new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true),
                "zlib" => new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true),
                "deflate" => new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true),
                _ => new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)
            };

            await using (compressor)
            {
                await input.CopyToAsync(compressor, 81920, ct);
            }
        }

        var resultSize = new FileInfo(outputPath).Length;
        sw.Stop();

        return new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["sourcePath"] = sourcePath, ["outputPath"] = outputPath,
                ["originalSize"] = originalSize, ["resultSize"] = resultSize,
                ["detectedFormat"] = detectedFormat, ["mode"] = mode,
                ["targetFormat"] = mode == "compress" ? targetFormat : detectedFormat,
                ["compressionRatio"] = originalSize > 0 ? Math.Round((double)resultSize / originalSize, 4) : 1.0
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
        var source = query.Source;

        if (Directory.Exists(source))
        {
            var limit = query.Limit ?? int.MaxValue;
            var offset = query.Offset ?? 0;
            var idx = 0;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (idx < offset) { idx++; continue; }
                if (idx - offset >= limit) break;

                var info = new FileInfo(file);
                var format = await DetectCompressionFormatAsync(file, ct);
                yield return new ProcessingResult
                {
                    Data = new Dictionary<string, object?>
                    {
                        ["filePath"] = file, ["fileName"] = info.Name, ["size"] = info.Length,
                        ["detectedFormat"] = format, ["isCompressed"] = format != "none"
                    },
                    Metadata = new ProcessingMetadata { RowsProcessed = 1, RowsReturned = 1, BytesProcessed = info.Length, ProcessingTimeMs = sw.Elapsed.TotalMilliseconds }
                };
                idx++;
            }
        }
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CompressionAggregationHelper.AggregateFileSizes(query, aggregationType, ct);
    }

    /// <summary>
    /// Detects the compression format of a file by reading its magic bytes.
    /// </summary>
    private static async Task<string> DetectCompressionFormatAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath)) return "none";

        var header = new byte[4];
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4, true);
        var read = await fs.ReadAsync(header, ct);
        if (read < 2) return "none";

        // Zstd magic: 0xFD2FB528 (little-endian: 28 B5 2F FD)
        if (read >= 4 && header[0] == 0x28 && header[1] == 0xB5 && header[2] == 0x2F && header[3] == 0xFD)
            return "zstd";

        // Gzip magic: 0x1F 0x8B
        if (header[0] == 0x1F && header[1] == 0x8B)
            return "gzip";

        // LZ4 frame magic: 0x04224D18 (little-endian: 04 22 4D 18)
        if (read >= 4 && header[0] == 0x04 && header[1] == 0x22 && header[2] == 0x4D && header[3] == 0x18)
            return "lz4";

        // ZLib magic: 0x78 (CMF byte with deflate method)
        if (header[0] == 0x78 && (header[1] == 0x01 || header[1] == 0x5E || header[1] == 0x9C || header[1] == 0xDA))
            return "zlib";

        // Brotli has no standard magic bytes, detect by extension
        if (filePath.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
            return "brotli";

        return "none";
    }

    private static T? GetOption<T>(ProcessingQuery query, string key)
    { if (query.Options?.TryGetValue(key, out var v) == true && v is T t) return t; return default; }

    private static ProcessingResult MakeError(string error, string source, Stopwatch sw)
    { sw.Stop(); return new ProcessingResult { Data = new Dictionary<string, object?> { ["error"] = error, ["source"] = source }, Metadata = new ProcessingMetadata { ProcessingTimeMs = sw.Elapsed.TotalMilliseconds } }; }
}
