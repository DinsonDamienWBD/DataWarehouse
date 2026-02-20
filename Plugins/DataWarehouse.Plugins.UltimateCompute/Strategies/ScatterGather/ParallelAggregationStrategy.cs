using System.Collections.Concurrent;
using System.Text;
using DataWarehouse.SDK.Contracts.Compute;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.ScatterGather;

/// <summary>
/// Parallel aggregation with map-side combiners, partition-level partial aggregates,
/// and final reduce phase.
/// </summary>
internal sealed class ParallelAggregationStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.scattergather.parallel-aggregation";
    /// <inheritdoc/>
    public override string StrategyName => "Parallel Aggregation";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 16L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(1),
        SupportedLanguages: ["any"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: false, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 50, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var input = task.GetInputDataAsString();
            var lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var partitionCount = Math.Max(1, task.ResourceLimits?.MaxThreads ?? Environment.ProcessorCount);
            var chunkSize = (int)Math.Ceiling((double)lines.Length / partitionCount);
            var chunks = lines.Chunk(chunkSize).ToArray();

            // MAP + COMBINE phase: parallel partial aggregation per partition
            var partialAggregates = new BoundedDictionary<string, long>(1000);

            await Parallel.ForEachAsync(
                Enumerable.Range(0, chunks.Length),
                new ParallelOptions { MaxDegreeOfParallelism = partitionCount, CancellationToken = cancellationToken },
                (idx, ct) =>
                {
                    // Map-side combiner: aggregate within partition
                    var localCounts = new Dictionary<string, long>();
                    foreach (var line in chunks[idx])
                    {
                        var key = line.Contains('\t') ? line[..line.IndexOf('\t')] : line.Trim();
                        if (string.IsNullOrEmpty(key)) continue;

                        var value = 1L;
                        if (line.Contains('\t'))
                        {
                            var valStr = line[(line.IndexOf('\t') + 1)..];
                            if (long.TryParse(valStr, out var parsed)) value = parsed;
                        }

                        localCounts[key] = localCounts.GetValueOrDefault(key) + value;
                    }

                    // Merge local into global partial aggregates
                    foreach (var (key, count) in localCounts)
                        partialAggregates.AddOrUpdate(key, count, (_, existing) => existing + count);

                    return ValueTask.CompletedTask;
                });

            // REDUCE: final aggregation (already done via ConcurrentDictionary merge)
            var output = new StringBuilder();
            foreach (var (key, total) in partialAggregates.OrderBy(kv => kv.Key))
                output.AppendLine($"{key}\t{total}");

            return (EncodeOutput(output.ToString()), $"Parallel aggregation: {lines.Length} lines, {chunks.Length} partitions, {partialAggregates.Count} unique keys");
        }, cancellationToken);
    }
}
