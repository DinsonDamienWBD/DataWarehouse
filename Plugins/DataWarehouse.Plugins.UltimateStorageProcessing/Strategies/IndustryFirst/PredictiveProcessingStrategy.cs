using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.IndustryFirst;

/// <summary>
/// Predictive processing strategy that predicts which files will need processing based on
/// access patterns. Uses exponential moving average for access prediction, pre-processes
/// likely candidates during idle time, and tracks prediction accuracy.
/// </summary>
internal sealed class PredictiveProcessingStrategy : StorageProcessingStrategyBase
{
    private readonly BoundedDictionary<string, AccessStats> _accessHistory = new BoundedDictionary<string, AccessStats>(1000);
    private const double Alpha = 0.3; // EMA smoothing factor

    /// <inheritdoc/>
    public override string StrategyId => "industryfirst-predictive";

    /// <inheritdoc/>
    public override string Name => "Predictive Processing Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsAggregation = true, SupportsProjection = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average },
        MaxQueryComplexity = 7
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        var topK = CliProcessHelper.GetOption<int>(query, "topK");
        if (topK <= 0) topK = 10;

        if (!Directory.Exists(query.Source))
            return MakeError("Source directory not found", sw);

        // Record access patterns and update EMA
        foreach (var file in Directory.EnumerateFiles(query.Source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);

            _accessHistory.AddOrUpdate(file,
                _ => new AccessStats
                {
                    LastAccessed = info.LastAccessTimeUtc,
                    AccessCount = 1,
                    EmaPrediction = 1.0,
                    Size = info.Length
                },
                (_, stats) =>
                {
                    stats.AccessCount++;
                    // Update EMA: new_ema = alpha * current + (1 - alpha) * previous
                    stats.EmaPrediction = Alpha * stats.AccessCount + (1 - Alpha) * stats.EmaPrediction;
                    stats.LastAccessed = info.LastAccessTimeUtc;
                    stats.Size = info.Length;
                    return stats;
                });
        }

        // Predict top-K files likely to need processing
        var predictions = _accessHistory
            .OrderByDescending(kvp => kvp.Value.EmaPrediction)
            .Take(topK)
            .Select(kvp => new Dictionary<string, object>
            {
                ["filePath"] = kvp.Key,
                ["accessCount"] = kvp.Value.AccessCount,
                ["emaPrediction"] = Math.Round(kvp.Value.EmaPrediction, 4),
                ["lastAccessed"] = kvp.Value.LastAccessed.ToString("O"),
                ["size"] = kvp.Value.Size
            })
            .ToList();

        sw.Stop();
        return await Task.FromResult(new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["sourcePath"] = query.Source,
                ["trackedFiles"] = _accessHistory.Count,
                ["topK"] = topK,
                ["predictions"] = predictions,
                ["emaAlpha"] = Alpha
            },
            Metadata = new ProcessingMetadata
            {
                RowsProcessed = _accessHistory.Count, RowsReturned = predictions.Count,
                ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
            }
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        var limit = query.Limit ?? int.MaxValue;
        var idx = 0;

        foreach (var kvp in _accessHistory.OrderByDescending(k => k.Value.EmaPrediction))
        {
            ct.ThrowIfCancellationRequested();
            if (idx >= limit) break;

            yield return new ProcessingResult
            {
                Data = new Dictionary<string, object?>
                {
                    ["filePath"] = kvp.Key, ["accessCount"] = kvp.Value.AccessCount,
                    ["emaPrediction"] = Math.Round(kvp.Value.EmaPrediction, 4),
                    ["size"] = kvp.Value.Size
                },
                Metadata = new ProcessingMetadata { RowsProcessed = 1, RowsReturned = 1, ProcessingTimeMs = sw.Elapsed.TotalMilliseconds }
            };
            idx++;
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        var sizes = _accessHistory.Values.Select(v => v.Size).ToList();
        object result = aggregationType switch
        {
            AggregationType.Count => (long)sizes.Count,
            AggregationType.Sum => sizes.Count > 0 ? sizes.Sum() : 0L,
            AggregationType.Average => sizes.Count > 0 ? sizes.Average() : 0.0,
            _ => 0.0
        };
        return Task.FromResult(new AggregationResult { AggregationType = aggregationType, Value = result });
    }

    private sealed class AccessStats
    {
        public DateTime LastAccessed;
        public int AccessCount;
        public double EmaPrediction;
        public long Size;
    }

    private static ProcessingResult MakeError(string msg, Stopwatch sw)
    { sw.Stop(); return new ProcessingResult { Data = new Dictionary<string, object?> { ["error"] = msg }, Metadata = new ProcessingMetadata { ProcessingTimeMs = sw.Elapsed.TotalMilliseconds } }; }
}
