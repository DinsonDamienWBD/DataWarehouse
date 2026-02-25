using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts.Compute;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.ScatterGather;

/// <summary>
/// Data redistribution strategy using hash-based or sort-merge shuffle.
/// Configurable partition count for redistributing data across workers.
/// </summary>
internal sealed class ShuffleStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.scattergather.shuffle";
    /// <inheritdoc/>
    public override string StrategyName => "Shuffle";
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
            var records = input.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var targetPartitions = task.ResourceLimits?.MaxThreads ?? Environment.ProcessorCount;

            var shuffleMode = "hash";
            if (task.Metadata?.TryGetValue("shuffle_mode", out var sm) == true && sm is string sms)
                shuffleMode = sms.ToLowerInvariant();

            // SHUFFLE phase: redistribute records by key
            var partitions = new List<string>[targetPartitions];
            for (var i = 0; i < targetPartitions; i++) partitions[i] = new List<string>();

            foreach (var record in records)
            {
                var key = record.Contains('\t') ? record[..record.IndexOf('\t')] : record;
                int partitionIdx;

                if (shuffleMode == "sort-merge")
                {
                    // Range-based partitioning via sort key comparison
                    var keyHash = key.GetHashCode();
                    partitionIdx = ((keyHash % targetPartitions) + targetPartitions) % targetPartitions;
                }
                else
                {
                    // Hash-based partitioning
                    var hash = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0);
                    partitionIdx = (int)(hash % (uint)targetPartitions);
                }

                partitions[partitionIdx].Add(record);
            }

            // Sort within each partition for sort-merge mode
            if (shuffleMode == "sort-merge")
            {
                Parallel.For(0, targetPartitions, i =>
                {
                    partitions[i].Sort(StringComparer.Ordinal);
                });
            }

            // Process each partition through the user code
            var codeStr = task.GetCodeAsString();
            var partitionOutputs = new BoundedDictionary<int, string>(1000);

            await Parallel.ForEachAsync(
                Enumerable.Range(0, targetPartitions),
                new ParallelOptions { MaxDegreeOfParallelism = targetPartitions, CancellationToken = cancellationToken },
                async (idx, ct) =>
                {
                    if (partitions[idx].Count == 0) { partitionOutputs[idx] = ""; return; }
                    var data = string.Join('\n', partitions[idx]);
                    var codePath = Path.GetTempFileName() + ".sh";
                    try
                    {
                        await File.WriteAllTextAsync(codePath, codeStr, ct);
                        var result = await RunProcessAsync("sh", $"\"{codePath}\"",
                            stdin: data, timeout: GetEffectiveTimeout(task), cancellationToken: ct);
                        partitionOutputs[idx] = result.StandardOutput;
                    }
                    finally { try { File.Delete(codePath); } catch { /* Best-effort cleanup — failure is non-fatal */ } }
                });

            // Merge outputs in partition order
            var output = new StringBuilder();
            foreach (var idx in Enumerable.Range(0, targetPartitions))
            {
                if (partitionOutputs.TryGetValue(idx, out var po))
                    output.Append(po);
            }

            var sizes = partitions.Select(p => p.Count);
            return (EncodeOutput(output.ToString()), $"Shuffle ({shuffleMode}): {records.Length} records -> {targetPartitions} partitions [{string.Join(",", sizes)}]");
        }, cancellationToken);
    }
}
