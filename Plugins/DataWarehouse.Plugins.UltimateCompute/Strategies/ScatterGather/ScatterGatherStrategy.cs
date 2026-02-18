using System.Collections.Concurrent;
using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.ScatterGather;

/// <summary>
/// Generic scatter-gather pattern that partitions input into N chunks,
/// executes each in parallel, and aggregates results.
/// </summary>
internal sealed class ScatterGatherStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.scattergather.generic";
    /// <inheritdoc/>
    public override string StrategyName => "Scatter-Gather";
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
            var partitionCount = task.ResourceLimits?.MaxThreads ?? Math.Min(Environment.ProcessorCount, lines.Length);
            if (partitionCount < 1) partitionCount = 1;

            // SCATTER: split input into chunks
            var chunkSize = (int)Math.Ceiling((double)lines.Length / partitionCount);
            var chunks = lines.Chunk(chunkSize).ToArray();

            // PARALLEL EXECUTION
            var results = new ConcurrentDictionary<int, string>();
            var codeStr = task.GetCodeAsString();

            await Parallel.ForEachAsync(
                Enumerable.Range(0, chunks.Length),
                new ParallelOptions { MaxDegreeOfParallelism = partitionCount, CancellationToken = cancellationToken },
                async (idx, ct) =>
                {
                    var chunkData = string.Join('\n', chunks[idx]);
                    var codePath = Path.GetTempFileName() + ".sh";
                    try
                    {
                        await File.WriteAllTextAsync(codePath, codeStr, ct);
                        var result = await RunProcessAsync("sh", $"\"{codePath}\"",
                            stdin: chunkData, timeout: GetEffectiveTimeout(task), cancellationToken: ct);
                        results[idx] = result.StandardOutput;
                    }
                    finally
                    {
                        try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
                    }
                });

            // GATHER: aggregate results in order
            var output = new StringBuilder();
            foreach (var idx in results.Keys.OrderBy(k => k))
                output.Append(results[idx]);

            return (EncodeOutput(output.ToString()), $"Scatter-Gather: {lines.Length} lines, {chunks.Length} partitions, {partitionCount} parallel");
        }, cancellationToken);
    }
}
