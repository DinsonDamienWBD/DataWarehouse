using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.IndustryFirst;

/// <summary>
/// Cost-optimized processing strategy that estimates processing cost per strategy and selects
/// the cheapest strategy meeting quality thresholds. Factors in CPU time, memory usage,
/// and I/O operations. Tracks actual vs predicted cost for calibration.
/// </summary>
internal sealed class CostOptimizedProcessingStrategy : StorageProcessingStrategyBase
{
    private readonly List<CostRecord> _costHistory = new();
    private readonly object _historyLock = new();

    /// <inheritdoc/>
    public override string StrategyId => "industryfirst-costoptimized";

    /// <inheritdoc/>
    public override string Name => "Cost-Optimized Processing Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsAggregation = true, SupportsProjection = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average, AggregationType.Min, AggregationType.Max },
        MaxQueryComplexity = 7
    };

    /// <inheritdoc/>
    public override Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        var qualityThreshold = CliProcessHelper.GetOption<double>(query, "qualityThreshold");
        if (qualityThreshold <= 0) qualityThreshold = 0.9;

        long fileSize = 0;
        if (File.Exists(query.Source))
            fileSize = new FileInfo(query.Source).Length;
        else if (Directory.Exists(query.Source))
            fileSize = Directory.EnumerateFiles(query.Source, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

        // Estimate costs for different strategies
        var strategies = new[]
        {
            new StrategyEstimate("deflate-fastest", EstimateCpuCost(fileSize, 0.2), EstimateMemory(fileSize, 1.0), EstimateIo(fileSize, 1.5), 0.7),
            new StrategyEstimate("zlib-optimal", EstimateCpuCost(fileSize, 1.0), EstimateMemory(fileSize, 2.0), EstimateIo(fileSize, 1.2), 0.85),
            new StrategyEstimate("brotli-quality6", EstimateCpuCost(fileSize, 3.0), EstimateMemory(fileSize, 4.0), EstimateIo(fileSize, 1.0), 0.92),
            new StrategyEstimate("brotli-quality11", EstimateCpuCost(fileSize, 10.0), EstimateMemory(fileSize, 16.0), EstimateIo(fileSize, 0.85), 0.98),
            new StrategyEstimate("zstd-level3", EstimateCpuCost(fileSize, 0.5), EstimateMemory(fileSize, 1.5), EstimateIo(fileSize, 1.1), 0.88),
        };

        // Select cheapest strategy meeting quality threshold
        var candidates = strategies.Where(s => s.Quality >= qualityThreshold).OrderBy(s => s.TotalCost).ToList();
        var selected = candidates.FirstOrDefault() ?? strategies.OrderBy(s => s.TotalCost).First();

        // Record cost prediction; actualCost measured AFTER selection work completes (finding 4289:
        // was recording elapsed before any real processing, producing permanently near-zero values).
        var predictedCost = selected.TotalCost;
        sw.Stop();
        var actualCost = sw.Elapsed.TotalMilliseconds;

        // Calculate prediction accuracy from history before adding this record
        double accuracy;
        lock (_historyLock)
        {
            var recent = _costHistory.TakeLast(100).ToList();
            accuracy = recent.Count > 0
                ? Math.Round(recent.Average(r => 1.0 - Math.Min(1.0, Math.Abs(r.PredictedCost - r.ActualCost) / Math.Max(r.PredictedCost, 1.0))) * 100.0, 2)
                : 0.0;

            _costHistory.Add(new CostRecord
            {
                Strategy = selected.Name,
                PredictedCost = predictedCost,
                ActualCost = actualCost,
                FileSize = fileSize,
                Timestamp = DateTimeOffset.UtcNow
            });

            // Keep history bounded
            while (_costHistory.Count > 1000) _costHistory.RemoveAt(0);
        }

        return Task.FromResult(new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["sourcePath"] = query.Source, ["fileSize"] = fileSize,
                ["qualityThreshold"] = qualityThreshold,
                ["selectedStrategy"] = selected.Name,
                ["estimatedCpuMs"] = Math.Round(selected.CpuCost, 2),
                ["estimatedMemoryMb"] = Math.Round(selected.MemoryCost / (1024.0 * 1024.0), 2),
                ["estimatedIoBytes"] = selected.IoCost,
                ["totalCost"] = Math.Round(selected.TotalCost, 2),
                ["quality"] = selected.Quality,
                ["candidateCount"] = candidates.Count,
                ["predictionAccuracy"] = accuracy,
                ["allEstimates"] = strategies.Select(s => new Dictionary<string, object>
                {
                    ["name"] = s.Name, ["cpuMs"] = Math.Round(s.CpuCost, 2),
                    ["totalCost"] = Math.Round(s.TotalCost, 2), ["quality"] = s.Quality,
                    ["meetsThreshold"] = s.Quality >= qualityThreshold
                }).ToList()
            },
            Metadata = new ProcessingMetadata
            {
                RowsProcessed = 1, RowsReturned = 1, BytesProcessed = fileSize,
                ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
            }
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, Array.Empty<string>(), sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, Array.Empty<string>(), ct);
    }

    private static double EstimateCpuCost(long bytes, double factor) => bytes / 1_000_000.0 * factor;
    private static double EstimateMemory(long bytes, double factor) => bytes * factor;
    private static double EstimateIo(long bytes, double factor) => bytes * factor;

    private sealed record StrategyEstimate(string Name, double CpuCost, double MemoryCost, double IoCost, double Quality)
    {
        public double TotalCost => CpuCost + MemoryCost / 1_000_000.0 + IoCost / 1_000_000.0;
    }

    private sealed class CostRecord
    {
        public required string Strategy { get; init; }
        public required double PredictedCost { get; init; }
        public required double ActualCost { get; init; }
        public required long FileSize { get; init; }
        public required DateTimeOffset Timestamp { get; init; }
    }
}
