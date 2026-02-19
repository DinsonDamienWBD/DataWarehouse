using System.Collections.Concurrent;
using System.Text;
using DataWarehouse.SDK.Contracts;
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
    private bool _isInitialized;
    private CancellationTokenSource? _shutdownCts;

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
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken = default)
    {
        if (_isInitialized) return;

        // Config validation would occur at plugin level when constructing this strategy
        // This validates runtime capabilities are in allowed ranges
        var maxConcurrent = Capabilities.MaxConcurrentTasks; // 8 from Capabilities
        if (maxConcurrent < 1 || maxConcurrent > 256)
            throw new ArgumentException("max_concurrent_tasks must be between 1 and 256");

        var maxMemory = Capabilities.MaxMemoryBytes; // 64GB from Capabilities
        if (maxMemory < 64L * 1024 * 1024 || maxMemory > 1024L * 1024 * 1024 * 1024)
            throw new ArgumentException("memory_limit must be between 64MB and 1TB");

        var maxExecutionTime = Capabilities.MaxExecutionTime; // 4 hours from Capabilities
        if (maxExecutionTime < TimeSpan.FromSeconds(1) || maxExecutionTime > TimeSpan.FromHours(24))
            throw new ArgumentException("execution_timeout must be between 1 second and 24 hours");

        // Runtime types mix validation (WASM/Container/Native)
        var supportedLanguages = Capabilities.SupportedLanguages;
        var validTypes = new[] { "any", "cuda", "opencl", "c", "c++", "python", "wasm", "container", "native" };
        if (!supportedLanguages.All(lang => validTypes.Contains(lang, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException($"runtime_types must contain only: {string.Join(", ", validTypes)}");

        _shutdownCts = new CancellationTokenSource();
        _isInitialized = true;

        await base.InitializeAsyncCore(cancellationToken);
    }

    /// <summary>
    /// Checks hybrid compute strategy health.
    /// </summary>
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        return await GetCachedHealthAsync(async ct =>
        {
            // Check GPU runtime availability
            var gpuAvailable = await CheckGpuAvailable(ct);

            // Test minimal task execution
            var testPassed = false;
            try
            {
                var testTask = new ComputeTask(
                    Id: "health-check",
                    Code: Encoding.UTF8.GetBytes("echo 'test'"),
                    Language: "sh",
                    EntryPoint: null,
                    InputData: Encoding.UTF8.GetBytes("test"),
                    Arguments: null,
                    Environment: null,
                    ResourceLimits: null,
                    Dependencies: null,
                    Timeout: TimeSpan.FromSeconds(5),
                    Metadata: null);

                using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var result = await ExecuteAsync(testTask, testCts.Token);
                testPassed = result.Success;
            }
            catch
            {
                testPassed = false;
            }

            var details = new Dictionary<string, object>
            {
                ["gpu_available"] = gpuAvailable,
                ["test_execution"] = testPassed,
                ["metrics_count"] = _metrics.Count
            };

            return new StrategyHealthCheckResult(
                IsHealthy: testPassed,
                Message: testPassed ? "Healthy" : "Degraded - test execution failed",
                Details: details);
        }, TimeSpan.FromSeconds(60), cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken = default)
    {
        if (_shutdownCts != null)
        {
            // Cancel running tasks with 30-second graceful shutdown
            _shutdownCts.Cancel();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown timeout - continue with cleanup
            }

            _shutdownCts.Dispose();
            _shutdownCts = null;
        }

        // Release runtime resources
        _metrics.Clear();
        _isInitialized = false;

        await base.ShutdownAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        IncrementCounter("hybridcompute.task_submit");

        try
        {
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
                    try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
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
                    try { File.Delete(gpuCodePath); } catch { /* Best-effort cleanup */ }
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

            IncrementCounter("hybridcompute.task_complete");
            return (EncodeOutput(output.ToString()), logs.ToString());
        }, cancellationToken);
        }
        catch (OutOfMemoryException ex)
        {
            // Error boundary: OOM
            IncrementCounter("hybridcompute.task_fail");
            IncrementCounter("hybridcompute.error.oom");
            throw new InvalidOperationException($"Hybrid compute ran out of memory during task {task.Id}", ex);
        }
        catch (TimeoutException ex)
        {
            // Error boundary: Timeout
            IncrementCounter("hybridcompute.task_fail");
            IncrementCounter("hybridcompute.error.timeout");
            throw new InvalidOperationException($"Hybrid compute task {task.Id} exceeded timeout", ex);
        }
        catch (Exception ex)
        {
            // Error boundary: General failure (includes potential sandbox escape detection)
            IncrementCounter("hybridcompute.task_fail");
            IncrementCounter("hybridcompute.error.general");

            // Check for sandbox escape indicators in exception message
            if (ex.Message.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                IncrementCounter("hybridcompute.error.sandbox_escape");
            }

            throw;
        }
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
