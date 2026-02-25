using System.Text;
using System.Threading.Channels;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.ScatterGather;

/// <summary>
/// Multi-stage pipeline execution with bounded channels for stage-to-stage streaming
/// and backpressure propagation.
/// </summary>
internal sealed class PipelinedExecutionStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.scattergather.pipelined";
    /// <inheritdoc/>
    public override string StrategyName => "Pipelined Execution";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 8L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(1),
        SupportedLanguages: ["any"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: false, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 20, MemoryIsolation: MemoryIsolationLevel.Process);
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
            var codeStr = task.GetCodeAsString();

            // Parse stage definitions from code (separated by "---STAGE---")
            var stageDelimiter = "---STAGE---";
            var stages = codeStr.Split(stageDelimiter, StringSplitOptions.RemoveEmptyEntries);
            if (stages.Length == 0) stages = [codeStr];

            var bufferSize = 100;
            if (task.Metadata?.TryGetValue("buffer_size", out var bs) == true && bs is int bsi)
                bufferSize = bsi;

            // Create bounded channels between stages
            var channels = new Channel<string>[stages.Length + 1];
            for (var i = 0; i <= stages.Length; i++)
                channels[i] = Channel.CreateBounded<string>(new BoundedChannelOptions(bufferSize)
                {
                    FullMode = BoundedChannelFullMode.Wait
                });

            // Feed input into first channel
            var feeder = Task.Run(async () =>
            {
                foreach (var line in lines)
                    await channels[0].Writer.WriteAsync(line, cancellationToken);
                channels[0].Writer.Complete();
            }, cancellationToken);

            // Create pipeline stages
            var stageTasks = new Task[stages.Length];
            for (var i = 0; i < stages.Length; i++)
            {
                var stageIdx = i;
                var stageCode = stages[stageIdx].Trim();
                var inputChannel = channels[stageIdx];
                var outputChannel = channels[stageIdx + 1];

                stageTasks[stageIdx] = Task.Run(async () =>
                {
                    var codePath = Path.GetTempFileName() + ".sh";
                    try
                    {
                        await File.WriteAllTextAsync(codePath, stageCode, cancellationToken);

                        await foreach (var item in inputChannel.Reader.ReadAllAsync(cancellationToken))
                        {
                            var result = await RunProcessAsync("sh", $"\"{codePath}\"",
                                stdin: item, timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);

                            foreach (var outLine in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                                await outputChannel.Writer.WriteAsync(outLine, cancellationToken);
                        }
                    }
                    finally
                    {
                        outputChannel.Writer.Complete();
                        try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
                    }
                }, cancellationToken);
            }

            await feeder;
            await Task.WhenAll(stageTasks);

            // Collect final output
            var output = new StringBuilder();
            await foreach (var line in channels[^1].Reader.ReadAllAsync(cancellationToken))
                output.AppendLine(line);

            return (EncodeOutput(output.ToString()), $"Pipeline: {stages.Length} stages, {lines.Length} input lines, buffer={bufferSize}");
        }, cancellationToken);
    }
}
