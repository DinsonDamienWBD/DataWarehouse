using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Distributed;

/// <summary>
/// MapReduce pattern implementation with input partitioning by key hash,
/// parallel map phase, shuffle sort, and reduce aggregation.
/// </summary>
internal sealed class MapReduceStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.distributed.mapreduce";
    /// <inheritdoc/>
    public override string StrategyName => "MapReduce";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 64L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(4),
        SupportedLanguages: ["mapreduce", "any"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: true, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 100, MemoryIsolation: MemoryIsolationLevel.Process);
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

            var partitionCount = task.ResourceLimits?.MaxThreads ?? Environment.ProcessorCount;
            var partitions = new List<string>[partitionCount];
            for (var i = 0; i < partitionCount; i++)
                partitions[i] = new List<string>();

            // Partition by hash of each line
            foreach (var line in lines)
            {
                var hash = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(line)), 0);
                partitions[hash % (uint)partitionCount].Add(line);
            }

            // MAP phase: process each partition in parallel
            var mapResults = new ConcurrentBag<KeyValuePair<string, string>>();
            var mapCode = task.GetCodeAsString();

            await Parallel.ForEachAsync(
                Enumerable.Range(0, partitionCount),
                new ParallelOptions { MaxDegreeOfParallelism = partitionCount, CancellationToken = cancellationToken },
                async (partIdx, ct) =>
                {
                    var partitionData = string.Join('\n', partitions[partIdx]);
                    if (string.IsNullOrEmpty(partitionData)) return;

                    var codePath = Path.GetTempFileName() + ".sh";
                    try
                    {
                        await File.WriteAllTextAsync(codePath, mapCode, ct);
                        var result = await RunProcessAsync("sh", $"\"{codePath}\"",
                            stdin: partitionData, timeout: GetEffectiveTimeout(task), cancellationToken: ct);

                        foreach (var outLine in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var tabIdx = outLine.IndexOf('\t');
                            var key = tabIdx > 0 ? outLine[..tabIdx] : outLine;
                            var value = tabIdx > 0 ? outLine[(tabIdx + 1)..] : "1";
                            mapResults.Add(new KeyValuePair<string, string>(key, value));
                        }
                    }
                    finally
                    {
                        try { File.Delete(codePath); } catch { }
                    }
                });

            // SHUFFLE: group by key
            var shuffled = mapResults
                .GroupBy(kv => kv.Key)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Select(kv => kv.Value).ToList());

            // REDUCE: aggregate values per key
            var output = new StringBuilder();
            foreach (var (key, values) in shuffled)
            {
                output.AppendLine($"{key}\t{values.Count}\t{string.Join(",", values)}");
            }

            var logs = $"MapReduce: {lines.Length} input lines, {partitionCount} partitions, {shuffled.Count} unique keys";
            return (EncodeOutput(output.ToString()), logs);
        }, cancellationToken);
    }
}
