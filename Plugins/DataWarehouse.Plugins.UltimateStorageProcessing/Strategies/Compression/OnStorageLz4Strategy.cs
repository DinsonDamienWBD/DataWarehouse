using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Compression;

/// <summary>
/// LZ4 on-storage compression strategy providing ultra-fast compression for real-time workloads.
/// Supports block and frame compression modes, block-independent mode for random access,
/// and HC mode for higher compression ratios at reduced speed.
/// </summary>
internal sealed class OnStorageLz4Strategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compression-lz4";

    /// <inheritdoc/>
    public override string Name => "On-Storage LZ4 Compression";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true,
        SupportsProjection = true,
        SupportsAggregation = true,
        SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average, AggregationType.Min, AggregationType.Max },
        MaxQueryComplexity = 4
    };

    /// <inheritdoc/>
    public override Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        // LZ4 native compression is not available in .NET BCL. A production implementation
        // requires a native LZ4 library (e.g. K4os.Compression.LZ4 NuGet package).
        // Previous implementation silently wrote fake LZ4 (Deflate stream with LZ4 magic header)
        // producing files that any real LZ4 reader would reject or corrupt.
        throw new NotSupportedException(
            "LZ4 compression requires a native LZ4 library (e.g. K4os.Compression.LZ4). " +
            "Add the package reference and implement using LZ4Stream/LZ4EncoderStream. " +
            "The previous implementation produced invalid LZ4 files (Deflate data with forged magic bytes).");
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();

        await foreach (var result in EnumerateFiles(query, ".lz4", sw, ct))
            yield return result;
    }

    /// <inheritdoc/>
    public override async Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query);
        ValidateAggregation(aggregationType);
        return await AggregateFileSizes(query, aggregationType, ct);
    }

    private static async IAsyncEnumerable<ProcessingResult> EnumerateFiles(ProcessingQuery query, string extension, Stopwatch sw, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(query.Source)) yield break;
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
                    ["filePath"] = file,
                    ["fileName"] = info.Name,
                    ["size"] = info.Length,
                    ["isCompressed"] = file.EndsWith(extension, StringComparison.OrdinalIgnoreCase),
                    ["lastModified"] = info.LastWriteTimeUtc.ToString("O")
                },
                Metadata = new ProcessingMetadata
                {
                    RowsProcessed = 1, RowsReturned = 1, BytesProcessed = info.Length,
                    ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
                }
            };
            idx++;
        }

        await Task.CompletedTask;
    }

    private static Task<AggregationResult> AggregateFileSizes(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct)
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
            AggregationType.Min => sizes.Count > 0 ? sizes.Min() : 0L,
            AggregationType.Max => sizes.Count > 0 ? sizes.Max() : 0L,
            _ => 0.0
        };

        // Finding 4252: synchronous return via Task.FromResult avoids state-machine allocation.
        return Task.FromResult(new AggregationResult
        {
            AggregationType = aggregationType,
            Value = value,
            Metadata = new ProcessingMetadata { RowsProcessed = sizes.Count, RowsReturned = 1, BytesProcessed = sizes.Sum(), ProcessingTimeMs = sw.Elapsed.TotalMilliseconds }
        });
    }

    private static T? GetOption<T>(ProcessingQuery query, string key)
    {
        if (query.Options?.TryGetValue(key, out var val) == true && val is T typed) return typed;
        return default;
    }

    private static ProcessingResult MakeError(string error, string source, Stopwatch sw)
    {
        sw.Stop();
        return new ProcessingResult
        {
            Data = new Dictionary<string, object?> { ["error"] = error, ["source"] = source },
            Metadata = new ProcessingMetadata { ProcessingTimeMs = sw.Elapsed.TotalMilliseconds }
        };
    }
}
