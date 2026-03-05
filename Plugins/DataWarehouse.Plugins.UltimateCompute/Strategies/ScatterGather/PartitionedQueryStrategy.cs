using DataWarehouse.SDK.Contracts.Compute;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.ScatterGather;

/// <summary>
/// Range/hash partitioned query execution with parallel partition scan and merge-sort results.
/// Splits queries by key range or hash and merges sorted outputs.
/// </summary>
internal sealed class PartitionedQueryStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.scattergather.partitioned-query";
    /// <inheritdoc/>
    public override string StrategyName => "Partitioned Query";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 16L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(1),
        SupportedLanguages: ["sql", "any"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: true, SupportsFileSystemAccess: true,
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
            var records = input.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var partitionCount = task.ResourceLimits?.MaxThreads ?? Environment.ProcessorCount;

            // Hash partition records
            var partitions = new List<string>[partitionCount];
            for (var i = 0; i < partitionCount; i++) partitions[i] = new List<string>();

            foreach (var record in records)
            {
                var key = record.Contains('\t') ? record[..record.IndexOf('\t')] : record;
                // Use StableHash to avoid per-record SHA256 allocation (O(n) allocations -> zero allocations).
                var hash = (uint)StableHash.Compute(key);
                partitions[hash % (uint)partitionCount].Add(record);
            }

            // Parallel partition scan with sort
            var partitionResults = new BoundedDictionary<int, List<string>>(1000);
            var codeStr = task.GetCodeAsString();

            await Parallel.ForEachAsync(
                Enumerable.Range(0, partitionCount),
                new ParallelOptions { MaxDegreeOfParallelism = partitionCount, CancellationToken = cancellationToken },
                async (idx, ct) =>
                {
                    if (partitions[idx].Count == 0) { partitionResults[idx] = []; return; }
                    var partData = string.Join('\n', partitions[idx]);
                    var codePath = Path.GetTempFileName() + ".sh";
                    try
                    {
                        await File.WriteAllTextAsync(codePath, codeStr, ct);
                        var result = await RunProcessAsync("sh", $"\"{codePath}\"",
                            stdin: partData, timeout: GetEffectiveTimeout(task), cancellationToken: ct);
                        var sorted = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).OrderBy(l => l).ToList();
                        partitionResults[idx] = sorted;
                    }
                    finally { try { File.Delete(codePath); } catch { /* Best-effort cleanup — failure is non-fatal */ } }
                });

            // Verify all partitions produced results; if any are missing, a partition task failed.
            var missingPartitions = Enumerable.Range(0, partitionCount)
                .Where(i => !partitionResults.ContainsKey(i))
                .ToList();
            if (missingPartitions.Count > 0)
                throw new InvalidOperationException(
                    $"Partitioned query failed: {missingPartitions.Count} partition(s) produced no output " +
                    $"(indices: {string.Join(", ", missingPartitions)}).");

            // Merge-sort across partitions
            var merged = partitionResults.Values.SelectMany(v => v).OrderBy(l => l);
            var output = string.Join('\n', merged);

            return (EncodeOutput(output), $"Partitioned query: {records.Length} records, {partitionCount} partitions, merge-sorted");
        }, cancellationToken);
    }
}
