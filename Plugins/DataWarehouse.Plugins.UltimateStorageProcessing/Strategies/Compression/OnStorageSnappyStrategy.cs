using System.Diagnostics;
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
    public override Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        // Snappy compression is not available in .NET BCL. A production implementation
        // requires a native Snappy library (e.g. Snappier or IronSnappy NuGet package).
        // Previous implementation silently wrote invalid Snappy files: "sNaPpY" framing header
        // followed by raw Deflate data, which no standard Snappy reader can process.
        throw new NotSupportedException(
            "Snappy compression requires a native Snappy library (e.g. Snappier or IronSnappy). " +
            "Add the package reference and implement using SnappyStream. " +
            "The previous implementation produced invalid Snappy files (Deflate data with forged magic bytes).");
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
