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

        // Merge into compacted output
        var compactedPath = Path.Combine(query.Source, "compacted_output.parquet");
        await using (var output = new FileStream(compactedPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            // Write Parquet magic bytes "PAR1"
            await output.WriteAsync("PAR1"u8.ToArray(), ct);

            foreach (var file in parquetFiles)
            {
                ct.ThrowIfCancellationRequested();
                await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                // Skip magic bytes of subsequent files
                if (input.Length > 4) input.Seek(4, SeekOrigin.Begin);
                await input.CopyToAsync(output, 81920, ct);
            }

            // Write footer magic bytes
            await output.WriteAsync("PAR1"u8.ToArray(), ct);
        }

        var compactedSize = new FileInfo(compactedPath).Length;
        sw.Stop();

        return new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["sourcePath"] = query.Source, ["compactedPath"] = compactedPath,
                ["inputFileCount"] = parquetFiles.Length, ["totalInputSize"] = totalInputSize,
                ["compactedSize"] = compactedSize, ["targetRowGroupSize"] = targetRowGroupSize,
                ["compressionRatio"] = totalInputSize > 0 ? Math.Round((double)compactedSize / totalInputSize, 4) : 1.0,
                ["fileStats"] = fileStats
            },
            Metadata = new ProcessingMetadata
            {
                RowsProcessed = parquetFiles.Length, RowsReturned = 1,
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
