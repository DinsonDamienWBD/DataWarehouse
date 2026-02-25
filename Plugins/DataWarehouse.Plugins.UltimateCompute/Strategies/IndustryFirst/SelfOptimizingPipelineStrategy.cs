using System.Text;
using DataWarehouse.SDK.Contracts.Compute;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.IndustryFirst;

/// <summary>
/// Self-optimizing pipeline that collects execution metrics and adjusts concurrency,
/// buffer sizes, and batch sizes using exponential moving average of throughput with
/// hill-climbing optimization to find optimal operating parameters.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline processes input in configurable batch sizes through multiple stages.
/// After each execution cycle, throughput (items/second) is measured and an EMA is computed.
/// The hill-climbing optimizer perturbs one parameter at a time (concurrency, batch size,
/// buffer size), measures the resulting throughput change, and keeps improvements. This
/// converges toward locally optimal pipeline configuration over repeated executions.
/// </para>
/// </remarks>
internal sealed class SelfOptimizingPipelineStrategy : ComputeRuntimeStrategyBase
{
    private readonly BoundedDictionary<string, PipelineConfig> _configs = new BoundedDictionary<string, PipelineConfig>(1000);
    private readonly BoundedDictionary<string, double> _emaThoughput = new BoundedDictionary<string, double>(1000);
    private const double EmaAlpha = 0.3; // Exponential moving average decay

    /// <inheritdoc/>
    public override string StrategyId => "compute.industryfirst.selfoptimizing";
    /// <inheritdoc/>
    public override string StrategyName => "Self-Optimizing Pipeline";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 32L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(4),
        SupportedLanguages: ["any"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: false, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 64, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var pipelineKey = task.Metadata?.TryGetValue("pipeline_key", out var pk) == true && pk is string pks
                ? pks
                : task.Language ?? "default";

            // Get or initialize pipeline configuration
            var config = _configs.GetOrAdd(pipelineKey, _ => new PipelineConfig(
                Concurrency: Math.Min(Environment.ProcessorCount, 8),
                BatchSize: 100,
                BufferSize: 1024,
                Iteration: 0));

            var input = task.GetInputDataAsString();
            var lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
                throw new ArgumentException("No input data for pipeline");

            // Create batches based on current config
            var batches = lines.Chunk(Math.Max(1, config.BatchSize)).ToArray();
            var results = new BoundedDictionary<int, string>(1000);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Execute pipeline with current parameters
            var codePath = Path.GetTempFileName() + ".sh";
            try
            {
                await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);

                using var semaphore = new SemaphoreSlim(Math.Max(1, config.Concurrency));
                var batchTasks = new List<Task>();

                for (var i = 0; i < batches.Length; i++)
                {
                    var batchIdx = i;
                    var batchData = string.Join('\n', batches[batchIdx]);

                    await semaphore.WaitAsync(cancellationToken);
                    batchTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var env = new Dictionary<string, string>(task.Environment ?? new Dictionary<string, string>())
                            {
                                ["PIPELINE_BATCH_IDX"] = batchIdx.ToString(),
                                ["PIPELINE_BATCH_SIZE"] = config.BatchSize.ToString(),
                                ["PIPELINE_CONCURRENCY"] = config.Concurrency.ToString(),
                                ["PIPELINE_BUFFER_SIZE"] = config.BufferSize.ToString()
                            };

                            var result = await RunProcessAsync("sh", $"\"{codePath}\"",
                                stdin: batchData, environment: env,
                                timeout: GetEffectiveTimeout(task),
                                cancellationToken: cancellationToken);

                            results[batchIdx] = result.StandardOutput;
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, cancellationToken));
                }

                await Task.WhenAll(batchTasks);
            }
            finally
            {
                try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
            }

            sw.Stop();

            // Calculate throughput
            var itemsProcessed = lines.Length;
            var throughput = itemsProcessed / Math.Max(0.001, sw.Elapsed.TotalSeconds);

            // Update EMA throughput
            var prevEma = _emaThoughput.GetOrAdd(pipelineKey, throughput);
            var newEma = EmaAlpha * throughput + (1.0 - EmaAlpha) * prevEma;
            _emaThoughput[pipelineKey] = newEma;

            // Hill-climbing optimization: perturb one parameter
            var newConfig = HillClimb(config, throughput, prevEma);
            _configs[pipelineKey] = newConfig;

            // Assemble output
            var output = new StringBuilder();
            foreach (var idx in results.Keys.OrderBy(k => k))
                output.Append(results[idx]);

            var logs = new StringBuilder();
            logs.AppendLine($"SelfOptimizing: {itemsProcessed} items in {sw.Elapsed.TotalMilliseconds:F0}ms");
            logs.AppendLine($"  Throughput: {throughput:F1} items/sec (EMA: {newEma:F1})");
            logs.AppendLine($"  Config: concurrency={config.Concurrency}, batch={config.BatchSize}, buffer={config.BufferSize}");
            logs.AppendLine($"  Next:   concurrency={newConfig.Concurrency}, batch={newConfig.BatchSize}, buffer={newConfig.BufferSize}");
            logs.AppendLine($"  Iteration: {config.Iteration} -> {newConfig.Iteration}");
            logs.AppendLine($"  Batches: {batches.Length}, Improvement: {(throughput > prevEma ? "YES" : "NO")}");

            return (EncodeOutput(output.ToString()), logs.ToString());
        }, cancellationToken);
    }

    /// <summary>
    /// Hill-climbing optimizer that perturbs one parameter at a time.
    /// If throughput improved, continue in the same direction.
    /// If throughput decreased, reverse direction and try a different parameter.
    /// </summary>
    private static PipelineConfig HillClimb(PipelineConfig config, double currentThroughput, double previousEma)
    {
        var improved = currentThroughput > previousEma;
        var iteration = config.Iteration + 1;

        // Cycle through parameters: 0=concurrency, 1=batchSize, 2=bufferSize
        var paramToAdjust = iteration % 3;

        // Direction: increase if improving, decrease if degrading
        var direction = improved ? 1 : -1;
        // Alternate direction on even/odd cycles for exploration
        if (iteration % 6 >= 3) direction *= -1;

        var newConcurrency = config.Concurrency;
        var newBatchSize = config.BatchSize;
        var newBufferSize = config.BufferSize;

        switch (paramToAdjust)
        {
            case 0: // Adjust concurrency (+/- 1)
                newConcurrency = Math.Clamp(config.Concurrency + direction, 1, Environment.ProcessorCount * 2);
                break;
            case 1: // Adjust batch size (+/- 25%)
                var batchDelta = Math.Max(1, config.BatchSize / 4);
                newBatchSize = Math.Clamp(config.BatchSize + direction * batchDelta, 1, 10_000);
                break;
            case 2: // Adjust buffer size (+/- 256)
                newBufferSize = Math.Clamp(config.BufferSize + direction * 256, 256, 65_536);
                break;
        }

        return new PipelineConfig(newConcurrency, newBatchSize, newBufferSize, iteration);
    }

    /// <summary>Pipeline configuration parameters subject to optimization.</summary>
    private record PipelineConfig(int Concurrency, int BatchSize, int BufferSize, int Iteration);
}
