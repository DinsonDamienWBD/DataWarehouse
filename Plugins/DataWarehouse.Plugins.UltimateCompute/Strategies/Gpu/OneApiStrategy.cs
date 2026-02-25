using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Gpu;

/// <summary>
/// Intel oneAPI compute strategy using icpx/DPC++ for SYCL kernel compilation.
/// Supports CPU, GPU, and FPGA device selection via the oneAPI runtime.
/// </summary>
internal sealed class OneApiStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.gpu.oneapi";
    /// <inheritdoc/>
    public override string StrategyName => "Intel oneAPI (SYCL)";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 64L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(4),
        SupportedLanguages: ["sycl", "dpc++", "c++"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: false, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 8, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("icpx", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var srcPath = Path.GetTempFileName() + ".cpp";
            var binPath = Path.GetTempFileName() + ".out";
            try
            {
                await File.WriteAllBytesAsync(srcPath, task.Code.ToArray(), cancellationToken);

                var device = "gpu";
                if (task.Metadata?.TryGetValue("oneapi_device", out var d) == true && d is string ds)
                    device = ds.ToLowerInvariant();

                // Compile with DPC++
                var compileFlags = $"-fsycl -fsycl-targets=spir64";
                if (device == "fpga")
                    compileFlags = "-fsycl -fintelfpga";

                var compileResult = await RunProcessAsync("icpx", $"{compileFlags} -o \"{binPath}\" \"{srcPath}\"",
                    timeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken);

                if (compileResult.ExitCode != 0)
                    throw new InvalidOperationException($"oneAPI compilation failed: {compileResult.StandardError}");

                // Set device selection via environment
                var env = new Dictionary<string, string>(task.Environment ?? new Dictionary<string, string>());
                env["ONEAPI_DEVICE_SELECTOR"] = device switch
                {
                    "cpu" => "opencl:cpu",
                    "gpu" => "level_zero:gpu",
                    "fpga" => "opencl:fpga",
                    _ => "*:*"
                };

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync(binPath, string.Join(" ", task.Arguments ?? []),
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: env, timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"oneAPI exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"oneAPI SYCL ({device}) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(srcPath); File.Delete(binPath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
