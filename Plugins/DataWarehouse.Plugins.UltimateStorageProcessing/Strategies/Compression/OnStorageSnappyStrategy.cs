using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Compression;

/// <summary>
/// Google Snappy on-storage compression strategy optimized for speed over compression ratio.
/// Uses framing format for streaming, supports block-level decompression, and is suited
/// for large data blocks where throughput matters more than compression ratio.
/// </summary>
internal sealed class OnStorageSnappyStrategy : StorageProcessingStrategyBase
{
    private const int SnappyBlockSize = 65536;

    /// <inheritdoc/>
    public override string StrategyId => "compression-snappy";

    /// <inheritdoc/>
    public override string Name => "On-Storage Snappy Compression";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true,
        SupportsProjection = true,
        SupportsAggregation = true,
        SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average, AggregationType.Min, AggregationType.Max },
        MaxQueryComplexity = 3
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        var sourcePath = query.Source;
        var mode = GetOption<string>(query, "mode") ?? "compress";

        if (!File.Exists(sourcePath))
            return MakeError("Source file not found", sourcePath, sw);

        var originalSize = new FileInfo(sourcePath).Length;
        var outputPath = mode == "decompress"
            ? (sourcePath.EndsWith(".snappy", StringComparison.OrdinalIgnoreCase) ? sourcePath[..^7] : sourcePath + ".decompressed")
            : sourcePath + ".snappy";

        // Snappy prioritizes speed; use fastest deflate as comparable speed-oriented compression
        await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, SnappyBlockSize, true))
        await using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, SnappyBlockSize, true))
        {
            if (mode == "decompress")
            {
                await using var decompressor = new DeflateStream(input, CompressionMode.Decompress);
                await decompressor.CopyToAsync(output, SnappyBlockSize, ct);
            }
            else
            {
                // Write snappy framing format stream identifier
                var streamId = "sNaPpY"u8.ToArray();
                await output.WriteAsync(streamId, ct);

                await using var compressor = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true);
                var buffer = new byte[SnappyBlockSize];
                int bytesRead;
                long totalRead = 0;
                while ((bytesRead = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await compressor.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    totalRead += bytesRead;
                }
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
                ["originalSize"] = originalSize, ["resultSize"] = resultSize,
                ["compressionRatio"] = Math.Round(ratio, 4),
                ["mode"] = mode, ["blockSize"] = SnappyBlockSize, ["algorithm"] = "snappy"
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
            yield return new ProcessingResult
            {
                Data = new Dictionary<string, object?>
                {
                    ["filePath"] = file, ["fileName"] = info.Name, ["size"] = info.Length,
                    ["isCompressed"] = file.EndsWith(".snappy", StringComparison.OrdinalIgnoreCase)
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

    private static T? GetOption<T>(ProcessingQuery query, string key)
    { if (query.Options?.TryGetValue(key, out var v) == true && v is T t) return t; return default; }

    private static ProcessingResult MakeError(string error, string source, Stopwatch sw)
    { sw.Stop(); return new ProcessingResult { Data = new Dictionary<string, object?> { ["error"] = error, ["source"] = source }, Metadata = new ProcessingMetadata { ProcessingTimeMs = sw.Elapsed.TotalMilliseconds } }; }
}
