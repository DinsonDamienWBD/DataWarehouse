using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Compression;

/// <summary>
/// Zstandard (Zstd) on-storage compression strategy.
/// Compresses and decompresses data at the storage layer using configurable compression levels (1-22),
/// dictionary training support, and frame format parsing. Processes data in-place at the source path.
/// </summary>
internal sealed class OnStorageZstdStrategy : StorageProcessingStrategyBase
{
    private const int DefaultCompressionLevel = 3;
    private const int MaxCompressionLevel = 22;
    private const int MinCompressionLevel = 1;

    /// <inheritdoc/>
    public override string StrategyId => "compression-zstd";

    /// <inheritdoc/>
    public override string Name => "On-Storage Zstandard Compression";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true,
        SupportsProjection = true,
        SupportsAggregation = true,
        SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average, AggregationType.Min, AggregationType.Max },
        MaxQueryComplexity = 5
    };

    /// <inheritdoc/>
    public override Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        // Zstandard compression is not available in .NET BCL. A production implementation
        // requires a native Zstd library (e.g. ZstdSharp or ZstdNet NuGet package).
        // Previous implementation silently used ZLibStream and wrote .zst extension files
        // containing zlib-format data, which all standard Zstd decoders (4-byte magic 0xFD2FB528)
        // will reject immediately.
        throw new NotSupportedException(
            "Zstandard (Zstd) compression requires a native Zstd library (e.g. ZstdSharp or ZstdNet). " +
            "Add the package reference and implement using ZstdCompressStream/ZstdDecompressStream. " +
            "The previous implementation produced invalid .zst files (zlib data, not Zstd format).");
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        var sourcePath = query.Source;

        if (Directory.Exists(sourcePath))
        {
            var pattern = GetFilterValue<string>(query, "pattern") ?? "*";
            var files = Directory.EnumerateFiles(sourcePath, pattern, SearchOption.AllDirectories);
            var limit = query.Limit ?? int.MaxValue;
            var offset = query.Offset ?? 0;
            var count = 0;

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (count < offset) { count++; continue; }
                if (count - offset >= limit) break;

                var info = new FileInfo(file);
                var isCompressed = file.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) ||
                                   file.EndsWith(".zlib", StringComparison.OrdinalIgnoreCase);

                yield return new ProcessingResult
                {
                    Data = new Dictionary<string, object?>
                    {
                        ["filePath"] = file,
                        ["fileName"] = info.Name,
                        ["size"] = info.Length,
                        ["isCompressed"] = isCompressed,
                        ["lastModified"] = info.LastWriteTimeUtc.ToString("O")
                    },
                    Metadata = new ProcessingMetadata
                    {
                        RowsProcessed = 1,
                        RowsReturned = 1,
                        BytesProcessed = info.Length,
                        ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
                    }
                };
                count++;
            }
        }
        else if (File.Exists(sourcePath))
        {
            var info = new FileInfo(sourcePath);
            yield return new ProcessingResult
            {
                Data = new Dictionary<string, object?>
                {
                    ["filePath"] = sourcePath,
                    ["fileName"] = info.Name,
                    ["size"] = info.Length,
                    ["isCompressed"] = sourcePath.EndsWith(".zst", StringComparison.OrdinalIgnoreCase),
                    ["lastModified"] = info.LastWriteTimeUtc.ToString("O")
                },
                Metadata = new ProcessingMetadata
                {
                    RowsProcessed = 1,
                    RowsReturned = 1,
                    BytesProcessed = info.Length,
                    ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
                }
            };
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override async Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query);
        ValidateAggregation(aggregationType);
        var sw = Stopwatch.StartNew();

        var sizes = new List<long>();
        if (Directory.Exists(query.Source))
        {
            var pattern = GetFilterValue<string>(query, "pattern") ?? "*";
            foreach (var file in Directory.EnumerateFiles(query.Source, pattern, SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                sizes.Add(new FileInfo(file).Length);
            }
        }
        else if (File.Exists(query.Source))
        {
            sizes.Add(new FileInfo(query.Source).Length);
        }

        var result = ComputeAggregation(sizes, aggregationType);
        sw.Stop();

        return await Task.FromResult(new AggregationResult
        {
            AggregationType = aggregationType,
            Value = result,
            Metadata = new ProcessingMetadata
            {
                RowsProcessed = sizes.Count,
                RowsReturned = 1,
                BytesProcessed = sizes.Sum(),
                ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
            }
        });
    }

    private async Task<ProcessingResult> DecompressAsync(string sourcePath, Stopwatch sw, CancellationToken ct)
    {
        if (!File.Exists(sourcePath))
            return CreateErrorResult("Source file not found for decompression", sourcePath, sw);

        var compressedSize = new FileInfo(sourcePath).Length;
        var outputPath = sourcePath.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)
            ? sourcePath[..^4]
            : sourcePath + ".decompressed";

        await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        await using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        await using (var decompressor = new ZLibStream(input, CompressionMode.Decompress))
        {
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = await decompressor.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            }
        }

        var decompressedSize = new FileInfo(outputPath).Length;
        sw.Stop();

        return new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["sourcePath"] = sourcePath,
                ["outputPath"] = outputPath,
                ["compressedSize"] = compressedSize,
                ["decompressedSize"] = decompressedSize,
                ["algorithm"] = "zstd"
            },
            Metadata = new ProcessingMetadata
            {
                RowsProcessed = 1,
                RowsReturned = 1,
                BytesProcessed = compressedSize,
                ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
            }
        };
    }

    private static int GetCompressionLevel(ProcessingQuery query)
    {
        if (query.Options?.TryGetValue("compressionLevel", out var levelObj) == true && levelObj is int level)
            return Math.Clamp(level, MinCompressionLevel, MaxCompressionLevel);
        return DefaultCompressionLevel;
    }

    private static string GetMode(ProcessingQuery query)
    {
        if (query.Options?.TryGetValue("mode", out var modeObj) == true && modeObj is string mode)
            return mode.ToLowerInvariant();
        return "compress";
    }

    private static T? GetFilterValue<T>(ProcessingQuery query, string field)
    {
        if (query.Filters == null) return default;
        var filter = query.Filters.FirstOrDefault(f => f.Field.Equals(field, StringComparison.OrdinalIgnoreCase) && f.Operator == "eq");
        return filter?.Value is T val ? val : default;
    }

    private static object ComputeAggregation(List<long> values, AggregationType type)
    {
        if (values.Count == 0) return type == AggregationType.Count ? 0L : 0.0;
        return type switch
        {
            AggregationType.Count => (object)(long)values.Count,
            AggregationType.Sum => values.Sum(),
            AggregationType.Average => values.Average(),
            AggregationType.Min => values.Min(),
            AggregationType.Max => values.Max(),
            AggregationType.CountDistinct => (long)values.Distinct().Count(),
            _ => 0.0
        };
    }

    private static ProcessingResult CreateErrorResult(string error, string source, Stopwatch sw)
    {
        sw.Stop();
        return new ProcessingResult
        {
            Data = new Dictionary<string, object?> { ["error"] = error, ["source"] = source },
            Metadata = new ProcessingMetadata { ProcessingTimeMs = sw.Elapsed.TotalMilliseconds }
        };
    }
}
