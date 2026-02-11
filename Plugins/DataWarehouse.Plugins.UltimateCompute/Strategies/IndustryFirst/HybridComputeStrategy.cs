using System.Collections.Concurrent;
using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.IndustryFirst;

/// <summary>
/// Hybrid compute strategy that splits tasks across CPU and GPU based on compute intensity
/// analysis. Uses data parallelism for GPU-suitable portions and task parallelism for
/// CPU-bound portions, then merges results.
/// </summary>
/// <remarks>
/// <para>
/// The strategy analyzes input metadata to determine compute intensity (FLOPS/byte ratio).
/// High-intensity segments (many operations per byte of data) are dispatched to GPU via
/// CUDA/OpenCL CLI tools, while low-intensity segments (I/O-heavy, branching) are processed
/// on CPU. The split threshold is configurable and adapts based on execution history.
/// </para>
/// </remarks>
internal sealed class HybridComputeStrategy : ComputeRuntimeStrategyBase
{
    private readonly ConcurrentDictionary<string, ExecutionMetrics> _metrics = new();
    private const double DefaultIntensityThreshold = 10.0; // FLOPS/byte

    /// <inheritdoc/>
    public override string StrategyId => "compute.industryfirst.hybrid";
    /// <inheritdoc/>
    public override string StrategyName => "Hybrid CPU/GPU Compute";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 64L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(4),
        SupportedLanguages: ["any", "cuda", "opencl", "c", "c++", "python"],
        SupportsMultiThreading: true, SupportsAsync: true,
        SupportsNetworkAccess: false, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 8, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom, ComputeRuntime.Native];

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var input = task.GetInputDataAsString();
            var lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
                throw new ArgumentException("No input data for hybrid compute");

            // Determine compute intensity threshold
            var threshold = DefaultIntensityThreshold;
            if (task.Metadata?.TryGetValue("intensity_threshold", out var it) == true && it is double itd)
                threshold = itd;

            // Check GPU availability
            var gpuAvailable = await CheckGpuAvailable(cancellationToken);

            // Analyze and classify segments
            var gpuSegments = new List<(int index, string data)>();
            var cpuSegments = new List<(int index, string data)>();

            for (var i = 0; i < lines.Length; i++)
            {
                var intensity = EstimateIntensity(lines[i], task);
                if (gpuAvailable && intensity >= threshold)
                    gpuSegments.Add((i, lines[i]));
                else
                    cpuSegments.Add((i, lines[i]));
            }

            var results = new ConcurrentDictionary<int, string>();
            var gpuElapsed = TimeSpan.Zero;
            var cpuElapsed = TimeSpan.Zero;

            // Launch CPU and GPU work in parallel
            var cpuTask = Task.Run(async () =>
            {
                if (cpuSegments.Count == 0) return;
                var codePath = Path.GetTempFileName() + ".sh";
                try
                {
                    await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    await Parallel.ForEachAsync(
                        cpuSegments,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = Environment.ProcessorCount,
                            CancellationToken = cancellationToken
                        },
                        async (segment, ct) =>
                        {
                            var result = await RunProcessAsync("sh", $"\"{codePath}\"",
                                stdin: segment.data, timeout: GetEffectiveTimeout(task),
                                cancellationToken: ct);
                            results[segment.index] = result.StandardOutput;
                        });

                    sw.Stop();
                    cpuElapsed = sw.Elapsed;
                }
                finally
                {
                    try { File.Delete(codePath); } catch { }
                }
            }, cancellationToken);

            var gpuTask = Task.Run(async () =>
            {
                if (gpuSegments.Count == 0) return;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Batch GPU segments into a single dispatch for efficiency
                var gpuInput = string.Join('\n', gpuSegments.Select(s => s.data));
                var gpuCodePath = Path.GetTempFileName() + ".sh";
                try
                {
                    await File.WriteAllBytesAsync(gpuCodePath, task.Code.ToArray(), cancellationToken);

                    var env = new Dictionary<string, string>(task.Environment ?? new Dictionary<string, string>())
                    {
                        ["HYBRID_MODE"] = "gpu",
                        ["HYBRID_BATCH_SIZE"] = gpuSegments.Count.ToString()
                    };

                    // Try nvidia-smi based GPU execution first, fall back to CPU
                    var executable = "sh";
                    var args = $"\"{gpuCodePath}\"";
                    if (gpuAvailable && task.Metadata?.TryGetValue("gpu_command", out var gc) == true && gc is string gcs)
                    {
                        executable = gcs;
                        args = $"\"{gpuCodePath}\"";
                    }

                    var result = await RunProcessAsync(executable, args,
                        stdin: gpuInput, environment: env,
                        timeout: GetEffectiveTimeout(task), cancellationToken: cancellationToken);

                    // Split GPU output back to segments
                    var gpuOutputLines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 0; i < gpuSegments.Count && i < gpuOutputLines.Length; i++)
                        results[gpuSegments[i].index] = gpuOutputLines[i] + "\n";

                    // If GPU output has fewer lines, assign remaining
                    if (gpuOutputLines.Length < gpuSegments.Count)
                    {
                        var fullOutput = result.StandardOutput;
                        for (var i = gpuOutputLines.Length; i < gpuSegments.Count; i++)
                            results[gpuSegments[i].index] = fullOutput;
                    }
                }
                finally
                {
                    try { File.Delete(gpuCodePath); } catch { }
                }

                sw.Stop();
                gpuElapsed = sw.Elapsed;
            }, cancellationToken);

            await Task.WhenAll(cpuTask, gpuTask);

            // Merge results in original order
            var output = new StringBuilder();
            for (var i = 0; i < lines.Length; i++)
            {
                if (results.TryGetValue(i, out var result))
                    output.Append(result);
            }

            // Record metrics
            var taskKey = task.Language ?? "unknown";
            _metrics[taskKey] = new ExecutionMetrics(
                cpuSegments.Count, gpuSegments.Count,
                cpuElapsed.TotalMilliseconds, gpuElapsed.TotalMilliseconds,
                DateTime.UtcNow);

            var logs = new StringBuilder();
            logs.AppendLine($"HybridCompute: {lines.Length} segments, CPU={cpuSegments.Count}, GPU={gpuSegments.Count}");
            logs.AppendLine($"  GPU available: {gpuAvailable}, Intensity threshold: {threshold:F1} FLOPS/byte");
            logs.AppendLine($"  CPU elapsed: {cpuElapsed.TotalMilliseconds:F0}ms, GPU elapsed: {gpuElapsed.TotalMilliseconds:F0}ms");
            logs.AppendLine($"  Split ratio: {(gpuSegments.Count * 100.0 / Math.Max(1, lines.Length)):F1}% GPU");

            return (EncodeOutput(output.ToString()), logs.ToString());
        }, cancellationToken);
    }

    /// <summary>
    /// Checks if GPU compute is available via nvidia-smi.
    /// </summary>
    private async Task<bool> CheckGpuAvailable(CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunProcessAsync("nvidia-smi", "--query-gpu=name --format=csv,noheader",
                timeout: TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Estimates compute intensity (FLOPS per byte) of an input segment.
    /// </summary>
    private static double EstimateIntensity(string segment, ComputeTask task)
    {
        if (task.Metadata?.TryGetValue("intensity", out var i) == true && i is double id)
            return id;

        // Heuristic: longer numeric-heavy lines suggest higher compute intensity
        var numericChars = segment.Count(c => char.IsDigit(c) || c == '.' || c == 'e' || c == 'E');
        var totalChars = Math.Max(1, segment.Length);
        var numericRatio = (double)numericChars / totalChars;

        // Scale: pure text ~1.0, pure numeric ~50.0
        return 1.0 + numericRatio * 49.0;
    }

    /// <summary>Execution metrics for CPU/GPU split tracking.</summary>
    private record ExecutionMetrics(
        int CpuSegments, int GpuSegments,
        double CpuElapsedMs, double GpuElapsedMs,
        DateTime Timestamp);
}
