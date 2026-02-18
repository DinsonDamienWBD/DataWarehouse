using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Gpu;

/// <summary>
/// NVIDIA CUDA compute strategy using nvcc for kernel compilation and nvidia-smi for device query.
/// Manages GPU memory allocation, kernel launch parameters, and execution timing.
/// </summary>
internal sealed class CudaStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.gpu.cuda";
    /// <inheritdoc/>
    public override string StrategyName => "NVIDIA CUDA";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 80L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(4),
        SupportedLanguages: ["cuda", "c++", "ptx"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: false, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 8, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("nvcc", "--version", cancellationToken);
        await IsToolAvailableAsync("nvidia-smi", "-L", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var cudaPath = Path.GetTempFileName() + ".cu";
            var binPath = Path.GetTempFileName() + ".out";
            try
            {
                await File.WriteAllBytesAsync(cudaPath, task.Code.ToArray(), cancellationToken);

                // Query GPU info
                var gpuInfo = await RunProcessAsync("nvidia-smi", "--query-gpu=name,memory.total,driver_version --format=csv,noheader",
                    timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);

                // Compile CUDA kernel
                var arch = "sm_70";
                if (task.Metadata?.TryGetValue("cuda_arch", out var ca) == true && ca is string cas)
                    arch = cas;

                var compileResult = await RunProcessAsync("nvcc", $"-arch={arch} -o \"{binPath}\" \"{cudaPath}\"",
                    timeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);

                if (compileResult.ExitCode != 0)
                    throw new InvalidOperationException($"CUDA compilation failed: {compileResult.StandardError}");

                // Execute the kernel
                var deviceId = 0;
                if (task.Metadata?.TryGetValue("device_id", out var di) == true && di is int did)
                    deviceId = did;

                var env = new Dictionary<string, string>(task.Environment ?? new Dictionary<string, string>())
                {
                    ["CUDA_VISIBLE_DEVICES"] = deviceId.ToString()
                };

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync(binPath, string.Join(" ", task.Arguments ?? []),
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: env, timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"CUDA kernel exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"CUDA ({arch}, device {deviceId}) completed in {result.Elapsed.TotalMilliseconds:F0}ms\nGPU: {gpuInfo.StandardOutput.Trim()}\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(cudaPath); File.Delete(binPath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
