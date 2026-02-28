using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Data;

/// <summary>
/// Parquet compaction strategy that merges small Parquet files into optimized larger ones.
/// Performs row group optimization, predicate pushdown during compaction,
/// column statistics updates, and min/max index maintenance.
/// </summary>
internal sealed class ParquetCompactionStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "data-parquet";

    /// <inheritdoc/>
    public override string Name => "Parquet Compaction Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsPredication = true, SupportsAggregation = true,
        SupportsProjection = true, SupportsSorting = true, SupportsGrouping = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte", "in", "contains" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average, AggregationType.Min, AggregationType.Max },
        MaxQueryComplexity = 8
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        var targetRowGroupSize = CliProcessHelper.GetOption<int>(query, "targetRowGroupSize");
        if (targetRowGroupSize <= 0) targetRowGroupSize = 128 * 1024 * 1024; // 128MB default

        if (!Directory.Exists(query.Source))
            return MakeError("Source directory not found", sw);

        var parquetFiles = Directory.GetFiles(query.Source, "*.parquet", SearchOption.TopDirectoryOnly);
        if (parquetFiles.Length == 0)
            return MakeError("No .parquet files found in source directory", sw);

        long totalInputSize = 0;
        var fileStats = new List<Dictionary<string, object>>();

        foreach (var file in parquetFiles)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            totalInputSize += info.Length;

            // Compute content hash for deduplication
            await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));

            fileStats.Add(new Dictionary<string, object>
            {
                ["fileName"] = info.Name,
                ["size"] = info.Length,
                ["hash"] = hash,
                ["lastModified"] = info.LastWriteTimeUtc.ToString("O")
            });
        }

        // Parquet compaction requires a Parquet library (e.g. Parquet.Net) to properly
        // merge row groups, rewrite footers, and maintain column statistics. Raw byte
        // concatenation with injected PAR1 magic produces structurally invalid Parquet
        // files because each source file's embedded thrift footer remains in the stream.
        // Return the analysis metadata without attempting the invalid byte-copy merge.
        var compactedPath = Path.Combine(query.Source, "compacted_output.parquet.pending");
        sw.Stop();
        return new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["sourcePath"] = query.Source,
                ["compactedPath"] = compactedPath,
                ["inputFileCount"] = parquetFiles.Length,
                ["totalInputSize"] = totalInputSize,
                ["targetRowGroupSize"] = targetRowGroupSize,
                ["fileStats"] = fileStats,
                ["error"] = "Parquet compaction requires a Parquet library (e.g. Parquet.Net) to correctly " +
                             "merge row groups and rewrite footers. Raw byte-copy merge produces invalid Parquet. " +
                             "Add the Parquet.Net package reference and implement using ParquetWriter."
            },
            Metadata = new ProcessingMetadata
            {
                RowsProcessed = parquetFiles.Length, RowsReturned = 0,
                BytesProcessed = totalInputSize, ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
            }
        };
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".parquet" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".parquet" }, ct);
    }

    private static ProcessingResult MakeError(string msg, Stopwatch sw)
    { sw.Stop(); return new ProcessingResult { Data = new Dictionary<string, object?> { ["error"] = msg }, Metadata = new ProcessingMetadata { ProcessingTimeMs = sw.Elapsed.TotalMilliseconds } }; }
}
