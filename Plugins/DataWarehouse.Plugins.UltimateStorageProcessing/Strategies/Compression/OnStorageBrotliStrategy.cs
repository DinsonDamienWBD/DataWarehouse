using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Compression;

/// <summary>
/// Brotli on-storage compression strategy optimized for text and web content.
/// Provides quality levels 0-11 with configurable window size, offering higher compression
/// ratios than gzip at the cost of compression speed. Ideal for static asset pre-compression.
/// </summary>
internal sealed class OnStorageBrotliStrategy : StorageProcessingStrategyBase
{
    private const int DefaultQuality = 4;
    private const int MaxQuality = 11;

    /// <inheritdoc/>
    public override string StrategyId => "compression-brotli";

    /// <inheritdoc/>
    public override string Name => "On-Storage Brotli Compression";

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
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        var sourcePath = query.Source;
        var mode = GetOption<string>(query, "mode") ?? "compress";
        // Use DefaultQuality only when quality is not explicitly set (option not present in query).
        // If quality IS set but is 0, respect the user's choice (fastest/no-op level).
        var rawQuality = GetOption<int?>(query, "quality");
        var quality = rawQuality.HasValue
            ? Math.Clamp(rawQuality.Value, 0, MaxQuality)
            : DefaultQuality;

        if (!File.Exists(sourcePath))
            return MakeResult(new Dictionary<string, object?> { ["error"] = "Source file not found" }, 0, sw);

        var originalSize = new FileInfo(sourcePath).Length;
        var outputPath = mode == "decompress"
            ? (sourcePath.EndsWith(".br", StringComparison.OrdinalIgnoreCase) ? sourcePath[..^3] : sourcePath + ".decompressed")
            : sourcePath + ".br";

        await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        await using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            if (mode == "decompress")
            {
                await using var decompressor = new BrotliStream(input, CompressionMode.Decompress);
                await decompressor.CopyToAsync(output, 81920, ct);
            }
            else
            {
                var level = quality <= 3 ? CompressionLevel.Fastest :
                            quality <= 8 ? CompressionLevel.Optimal : CompressionLevel.SmallestSize;
                await using var compressor = new BrotliStream(output, level, leaveOpen: true);
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
                ["sourcePath"] = sourcePath,
                ["outputPath"] = outputPath,
                ["originalSize"] = originalSize,
                ["resultSize"] = resultSize,
                ["compressionRatio"] = Math.Round(ratio, 4),
                ["spaceSavings"] = Math.Round((1.0 - ratio) * 100.0, 2),
                ["quality"] = quality,
                ["mode"] = mode,
                ["algorithm"] = "brotli"
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
                    ["isCompressed"] = file.EndsWith(".br", StringComparison.OrdinalIgnoreCase),
                    ["lastModified"] = info.LastWriteTimeUtc.ToString("O")
                },
                Metadata = new ProcessingMetadata { RowsProcessed = 1, RowsReturned = 1, BytesProcessed = info.Length, ProcessingTimeMs = sw.Elapsed.TotalMilliseconds }
            };
            idx++;
        }
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query);
        ValidateAggregation(aggregationType);
        return CompressionAggregationHelper.AggregateFileSizes(query, aggregationType, ct);
    }

    private static T? GetOption<T>(ProcessingQuery query, string key)
    {
        if (query.Options?.TryGetValue(key, out var v) == true && v is T t) return t;
        return default;
    }

    private static ProcessingResult MakeResult(Dictionary<string, object?> data, long bytes, Stopwatch sw)
    {
        sw.Stop();
        return new ProcessingResult { Data = data, Metadata = new ProcessingMetadata { BytesProcessed = bytes, ProcessingTimeMs = sw.Elapsed.TotalMilliseconds } };
    }
}

/// <summary>
/// Shared aggregation helper for compression strategies that compute file-based aggregations.
/// </summary>
internal static class CompressionAggregationHelper
{
    /// <summary>
    /// Aggregates file sizes from the source path.
    /// </summary>
    public static Task<AggregationResult> AggregateFileSizes(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var sizes = new List<long>();

        if (Directory.Exists(query.Source))
            foreach (var f in Directory.EnumerateFiles(query.Source, "*", SearchOption.AllDirectories))
            { ct.ThrowIfCancellationRequested(); sizes.Add(new FileInfo(f).Length); }
        else if (File.Exists(query.Source))
            sizes.Add(new FileInfo(query.Source).Length);

        sw.Stop();
        object value = aggregationType switch
        {
            AggregationType.Count => (long)sizes.Count,
            AggregationType.Sum => sizes.Count > 0 ? sizes.Sum() : 0L,
            AggregationType.Average => sizes.Count > 0 ? sizes.Average() : 0.0,
            AggregationType.Min => sizes.Count > 0 ? (object)sizes.Min() : 0L,
            AggregationType.Max => sizes.Count > 0 ? (object)sizes.Max() : 0L,
            AggregationType.CountDistinct => (long)sizes.Distinct().Count(),
            _ => 0.0
        };

        return Task.FromResult(new AggregationResult
        {
            AggregationType = aggregationType,
            Value = value,
            Metadata = new ProcessingMetadata { RowsProcessed = sizes.Count, RowsReturned = 1, BytesProcessed = sizes.Sum(), ProcessingTimeMs = sw.Elapsed.TotalMilliseconds }
        });
    }
}
